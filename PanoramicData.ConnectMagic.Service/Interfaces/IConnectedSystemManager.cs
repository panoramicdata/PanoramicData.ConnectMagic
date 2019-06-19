﻿using PanoramicData.ConnectMagic.Service.Models;
using System.Threading;
using System.Threading.Tasks;

namespace PanoramicData.ConnectMagic.Service.Interfaces
{
	public interface IConnectedSystemManager
	{
		Task RefreshDataSetsAsync(CancellationToken cancellationToken);

		Task<object> QueryLookupAsync(string query, string field);

		ConnectedSystemStats Stats { get; }

		ConnectedSystem ConnectedSystem { get; }
	}
}
