﻿using PanoramicData.ConnectMagic.Service.Models;
using System.Threading;
using System.Threading.Tasks;

namespace PanoramicData.ConnectMagic.Service.Interfaces
{
	public interface IConnectedSystemManager
	{
		Task RefreshDataSetAsync(ConnectedSystemDataSet dataSet, CancellationToken cancellationToken);

		Task<object?> QueryLookupAsync(
			QueryConfig queryConfig,
			string field,
			bool valueIfZeroMatchesFoundSets,
			object? valueIfZeroMatchesFound,
			bool valueIfMultipleMatchesFoundSets,
			object? valueIfMultipleMatchesFound,
			CancellationToken cancellationToken);

		ConnectedSystemStats Stats { get; }

		ConnectedSystem ConnectedSystem { get; }

		Task ClearCacheAsync();
	}
}
