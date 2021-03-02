﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// ------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Tracing;
    using Microsoft.Azure.Documents;

    internal sealed class FeedRangePartitionKeyRangeExtractor : IFeedRangeAsyncVisitor<IReadOnlyList<Documents.Routing.Range<string>>>
    {
        private readonly ContainerInternal container;

        public FeedRangePartitionKeyRangeExtractor(ContainerInternal container)
        {
            this.container = container ?? throw new ArgumentNullException(nameof(container));
        }

        public async Task<IReadOnlyList<Documents.Routing.Range<string>>> VisitAsync(FeedRangeLogicalPartitionKey feedRange, CancellationToken cancellationToken = default)
        {
            Routing.PartitionKeyRangeCache partitionKeyRangeCache = await this.container.ClientContext.DocumentClient.GetPartitionKeyRangeCacheAsync();
            PartitionKeyDefinition partitionKeyDefinition = await this.container.GetPartitionKeyDefinitionAsync(cancellationToken);
            return await feedRange.GetEffectiveRangesAsync(
                partitionKeyRangeCache,
                await this.container.GetCachedRIDAsync(
                    forceRefresh: false, 
                    NoOpTrace.Singleton, 
                    cancellationToken: cancellationToken),
                partitionKeyDefinition);
        }

        public async Task<IReadOnlyList<Documents.Routing.Range<string>>> VisitAsync(FeedRangePhysicalPartitionKeyRange feedRange, CancellationToken cancellationToken = default)
        {
            // Migration from PKRangeId scenario
            Routing.PartitionKeyRangeCache partitionKeyRangeCache = await this.container.ClientContext.DocumentClient.GetPartitionKeyRangeCacheAsync();
            return await feedRange.GetEffectiveRangesAsync(
                routingMapProvider: partitionKeyRangeCache,
                containerRid: await this.container.GetCachedRIDAsync(
                     forceRefresh: false, 
                     NoOpTrace.Singleton, 
                     cancellationToken: cancellationToken),
                partitionKeyDefinition: null);
        }

        public async Task<IReadOnlyList<Documents.Routing.Range<string>>> VisitAsync(FeedRangeEpkRange feedRange, CancellationToken cancellationToken = default)
        {
            Routing.PartitionKeyRangeCache partitionKeyRangeCache = await this.container.ClientContext.DocumentClient.GetPartitionKeyRangeCacheAsync();
            IReadOnlyList<PartitionKeyRange> pkRanges = await partitionKeyRangeCache.TryGetOverlappingRangesAsync(
                collectionRid: await this.container.GetCachedRIDAsync(
                    forceRefresh: false,
                    NoOpTrace.Singleton, 
                    cancellationToken: cancellationToken),
                range: new Documents.Routing.Range<string>(feedRange.StartEpkInclusive, feedRange.EndEpkExclusive, isMinInclusive: true, isMaxInclusive: false),
                trace: NoOpTrace.Singleton,
                forceRefresh: false);
            return pkRanges.Select(pkRange => pkRange.ToRange()).ToList();
        }
    }
}
