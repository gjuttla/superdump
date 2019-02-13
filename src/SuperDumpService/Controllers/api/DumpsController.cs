﻿using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using SuperDumpService.Models;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using SuperDumpService.Services;
using SuperDumpService.Helpers;
using SuperDump.Models;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Linq;
using System.Text;
using SuperDumpService.ViewModels;

// For more information on enabling Web API for empty projects, visit http://go.microsoft.com/fwlink/?LinkID=397860

namespace SuperDumpService.Controllers.Api {
	[Route("api/[controller]")]
	public class DumpsController : Controller {
		public SuperDumpRepository superDumpRepo;
		public BundleRepository bundleRepo;
		public DumpRepository dumpRepo;
		private readonly ILogger<DumpsController> logger;
		private readonly SearchService searchService;

		public DumpsController(
				SuperDumpRepository superDumpRepo,
				BundleRepository bundleRepo,
				DumpRepository dumpRepo,
				ILoggerFactory loggerFactory,
				SearchService searchService) {
			this.superDumpRepo = superDumpRepo;
			this.bundleRepo = bundleRepo;
			this.dumpRepo = dumpRepo;
			logger = loggerFactory.CreateLogger<DumpsController>();
			this.searchService = searchService;
		}

		/// <summary>
		/// Returns analysis data for requested bundle
		/// </summary>
		/// <param name="bundleId">ID of the requested bundle or dump</param>
		/// <returns>JSON array, if id was a bundle id, or a single JSON entry for a dump id</returns>
		/// <response code="200">Returned JSON data for all dumps in bundle</response>
		/// <response code="404">If result is not ready, or dump does not exist</response>
		[Authorize(Policy = LdapCookieAuthenticationExtension.UserPolicy)]
		[HttpGet("{bundleId}", Name = "dumps")]
		[ProducesResponseType(typeof(List<SDResult>), 200)]
		[ProducesResponseType(typeof(string), 404)]
		public async Task<IActionResult> Get(string bundleId) {
			// check if it is a bundle 
			var bundleInfo = superDumpRepo.GetBundle(bundleId);
			if (bundleInfo == null) {
				logger.LogNotFound("Api: Bundle not found", HttpContext, "BundleId", bundleId);
				return NotFound("Resource not found");
			}
			logger.LogBundleAccess("Api get Bundle", HttpContext, bundleInfo);
			var resultList = new List<SDResult>();
			foreach (var dumpInfo in dumpRepo.Get(bundleId)) {
				resultList.Add(await superDumpRepo.GetResult(dumpInfo.Id));
			}
			return Content(JsonConvert.SerializeObject(resultList, Formatting.Indented, new JsonSerializerSettings {
				ReferenceLoopHandling = ReferenceLoopHandling.Ignore
			}), "application/json");
		}

		/// <summary>
		/// Creates a new DumpBundle object on the server
		/// </summary>
		/// <param name="input">DumpBundle that's gonna be stored on server</param>
		/// <returns>Created resource</returns>
		/// <response code="400">If url was invalid, or SuperDump had an error when processing</response>
		/// <response code="201"></response>
		[HttpPost]
		[ProducesResponseType(typeof(void), 201)]
		[ProducesResponseType(typeof(string), 400)]
		public IActionResult Post([FromBody]DumpAnalysisInput input) {
			if (ModelState.IsValid) {

				string filename = input.UrlFilename;
				//validate URL
				if (Utility.ValidateUrl(input.Url, ref filename)) {
					if (filename == null && Utility.IsLocalFile(input.Url)) {
						filename = Path.GetFileName(input.Url);
					}
					string bundleId = superDumpRepo.ProcessInputfile(filename, input);
					if (bundleId != null) {
						logger.LogFileUpload("Api Upload", HttpContext, bundleId, input.CustomProperties, input.Url);
						return CreatedAtAction(nameof(HomeController.BundleCreated), "Home", new { bundleId = bundleId }, null);
					} else {
						// in case the input was just symbol files, we don't get a bundleid.
						// TODO
						throw new NotImplementedException();
					}
				} else {
					logger.LogNotFound("Api Upload: File not found", HttpContext, "Url", input.Url);
					return BadRequest("Invalid request, resource identifier is not valid or cannot be reached.");
				}
			} else {
				var errors = string.Join("; ", ModelState.Values.SelectMany(v => v.Errors).Select(x => "'" + x.Exception.Message + "'"));
				return BadRequest($"Invalid request, check if value was set: {errors}");
			}
		}

		/// <summary>
		/// Returns a calendar heatmap (count of found dumps per hour)
		/// 
		/// Can always be filtered by time (via <param name="start"/>, <param name="stop"/>)
		/// Search can either filter by
		///    - <param name="searchFilter">simple search query</param>
		///    - <param name="elasticSearchFilter">elasticsearch query</param>
		///    - duplicates of a specific dump by setting <param name="duplBundleId"/> and <param name="duplDumpId"/>
		/// </summary>
		/// <returns>json that corresponds to https://cal-heatmap.com/#data-format</returns>
		[HttpGet("Heatmap")]
		[ProducesResponseType(200)]
		[ProducesResponseType(typeof(string), 404)]
		public async Task<IActionResult> Heatmap(
				[FromQuery]DateTime start,
				[FromQuery]DateTime stop,
				[FromQuery]string searchFilter,
				[FromQuery]string elasticSearchFilter,
				[FromQuery]string duplBundleId, // in case of duplication search
				[FromQuery]string duplDumpId    // in case of duplication search
			) {

			IEnumerable<DumpViewModel> dumpViewModels = null;
			if (!string.IsNullOrEmpty(duplBundleId) && !string.IsNullOrEmpty(duplDumpId)) {
				// find duplicates of given bundleId+dumpId
				dumpViewModels = await searchService.SearchDuplicates(DumpIdentifier.Create(duplBundleId, duplDumpId), false);
			} else if(!string.IsNullOrEmpty(elasticSearchFilter)) {
				// run elasticsearch query
				dumpViewModels = await searchService.SearchByElasticFilter(elasticSearchFilter, false);
			} else {
				// do plain search, or show all of searchFilter is empty
				dumpViewModels = await searchService.SearchBySimpleFilter(searchFilter, false);
			}

			// apply timefilter
			dumpViewModels = dumpViewModels.Where(x => x.DumpInfo.Created >= start && x.DumpInfo.Created <= stop);

			int groupTime = (int)TimeSpan.FromHours(1).TotalSeconds; // group by hour
			var dumps = dumpViewModels.ToLookup(x => (x.DumpInfo.Created.ToUnixTimestamp() / groupTime) * groupTime);
			return Content(ToCalHeatmapJson(dumps), "application/json");
		}

		/// <summary>
		/// serialization for cal-heatmap format (https://cal-heatmap.com/#data-format)
		/// custom implementation to avoid the hassle of json converters for this simple format
		/// 
		/// cal-heatmap data: 
		///
		///   {
		///     "timestamp": value,
		///     "timestamp2": value2,
		///     ...
		///   }
		/// 
		/// </summary>
		private static string ToCalHeatmapJson(ILookup<int, DumpViewModel> dumps) {
			var sb = new StringBuilder();
			sb.Append("{");
			int i = 0;
			foreach (var group in dumps.OrderBy(x => x.Key)) {
				if (i > 0) sb.Append(", ");
				sb.Append("\"" + group.Key + "\": " + group.Count());
				i++;
			}
			sb.Append("}");
			return sb.ToString();
		}
	}
}
