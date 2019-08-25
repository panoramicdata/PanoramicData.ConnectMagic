﻿using System.Runtime.Serialization;

namespace PanoramicData.ConnectMagic.Service.Models
{
	/// <summary>
	/// A QueryConfig
	/// </summary>
	[DataContract]
	public class QueryConfig
	{
		/// <summary>
		/// The type of entity being queried
		/// </summary>
		[DataMember(Name = "Type")]
		public string Type { get; set; }

		/// <summary>
		/// The Get List query
		/// Syntax varies per ConnectedSystemType
		/// </summary>
		[DataMember(Name = "Query")]
		public string Query { get; set; }

		/// <summary>
		/// The query for the Delete action.
		/// Syntax varies per ConnectedSystemType.
		/// </summary>
		[DataMember(Name = "CreateQuery")]
		public string CreateQuery { get; set; }

		/// <summary>
		/// The query for the Delete action.
		/// Syntax varies per ConnectedSystemType.
		/// </summary>
		[DataMember(Name = "UpdateQuery")]
		public string UpdateQuery { get; set; }

		/// <summary>
		/// The query for the Delete action.
		/// Syntax varies per ConnectedSystemType.
		/// </summary>
		[DataMember(Name = "DeleteQuery")]
		public string DeleteQuery { get; set; }
	}
}