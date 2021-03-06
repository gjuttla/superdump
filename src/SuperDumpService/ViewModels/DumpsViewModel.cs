﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using X.PagedList;

namespace SuperDumpService.ViewModels {
	public class DumpsViewModel {
		public IEnumerable<DumpViewModel> Filtered { get; set; }
		public IPagedList<DumpViewModel> Paged { get; set; }
		public bool IsPopulated { get; set; }
		public bool IsRelationshipsPopulated { get; set; }
		public bool IsJiraIssuesPopulated { get; set; }
		public bool UseAutomaticDumpDeletion { get; set; }
		public string KibanaUrl { get; set; }
	}
}