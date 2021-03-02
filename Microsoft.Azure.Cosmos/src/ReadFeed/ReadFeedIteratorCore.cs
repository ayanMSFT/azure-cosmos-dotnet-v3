﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.ReadFeed
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Pagination;
    using Microsoft.Azure.Cosmos.Query.Core;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;
    using Microsoft.Azure.Cosmos.ReadFeed.Pagination;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Cosmos.Tracing;

    /// <summary>
    /// Cosmos feed stream iterator. This is used to get the query responses with a Stream content
    /// </summary>
    internal sealed class ReadFeedIteratorCore : FeedIteratorInternal
    {
        private readonly TryCatch<CrossPartitionReadFeedAsyncEnumerator> monadicEnumerator;
        private readonly QueryRequestOptions queryRequestOptions;
        private bool hasMoreResults;

        public ReadFeedIteratorCore(
            IDocumentContainer documentContainer,
            string continuationToken,
            ReadFeedPaginationOptions readFeedPaginationOptions,
            QueryRequestOptions queryRequestOptions,
            CancellationToken cancellationToken)
        {
            this.queryRequestOptions = queryRequestOptions;
            readFeedPaginationOptions ??= ReadFeedPaginationOptions.Default;

            if (!string.IsNullOrEmpty(continuationToken))
            {
                bool isNewArrayFormat = (continuationToken.Length >= 2) && (continuationToken[0] == '[') && (continuationToken[continuationToken.Length - 1] == ']');
                if (!isNewArrayFormat)
                {
                    // One of the two older formats
                    if (!FeedRangeContinuation.TryParse(continuationToken, out FeedRangeContinuation feedRangeContinuation))
                    {
                        // Backward compatible with old format
                        feedRangeContinuation = new FeedRangeCompositeContinuation(
                            containerRid: string.Empty,
                            FeedRangeEpkRange.FullRange,
                            new List<Documents.Routing.Range<string>>()
                            {
                                new Documents.Routing.Range<string>(
                                    Documents.Routing.PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey,
                                    Documents.Routing.PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey,
                                    isMinInclusive: true,
                                    isMaxInclusive: false)
                            },
                            continuationToken);
                    }

                    // need to massage it a little
                    List<CosmosElement> feedRangeStates = new List<CosmosElement>();
                    string oldContinuationFormat = feedRangeContinuation.ToString();
                    if (feedRangeContinuation.FeedRange is FeedRangeLogicalPartitionKey feedRangePartitionKey)
                    {
                        CosmosObject cosmosObject = CosmosObject.Parse(oldContinuationFormat);
                        CosmosArray continuations = (CosmosArray)cosmosObject["Continuation"];
                        if (continuations.Count != 1)
                        {
                            throw new InvalidOperationException("Expected only one continuation for partition key queries");
                        }

                        CosmosElement continuation = continuations[0];
                        CosmosObject continuationObject = (CosmosObject)continuation;
                        CosmosElement token = continuationObject["token"];
                        ReadFeedState state;
                        if (token is CosmosNull)
                        {
                            state = ReadFeedState.Beginning();
                        }
                        else
                        {
                            CosmosString tokenAsString = (CosmosString)token;
                            state = ReadFeedState.Continuation(CosmosElement.Parse(tokenAsString.Value));
                        }

                        FeedRangeState<ReadFeedState> feedRangeState = new FeedRangeState<ReadFeedState>(feedRangePartitionKey, state);
                        feedRangeStates.Add(ReadFeedFeedRangeStateSerializer.ToCosmosElement(feedRangeState));
                    }
                    else
                    {
                        CosmosObject cosmosObject = CosmosObject.Parse(oldContinuationFormat);
                        CosmosArray continuations = (CosmosArray)cosmosObject["Continuation"];

                        foreach (CosmosElement continuation in continuations)
                        {
                            CosmosObject continuationObject = (CosmosObject)continuation;
                            CosmosObject rangeObject = (CosmosObject)continuationObject["range"];
                            string min = ((CosmosString)rangeObject["min"]).Value;
                            string max = ((CosmosString)rangeObject["max"]).Value;
                            CosmosElement token = continuationObject["token"];

                            FeedRangeInternal feedRange = new FeedRangeEpkRange(min, max);
                            ReadFeedState state;
                            if (token is CosmosNull)
                            {
                                state = ReadFeedState.Beginning();
                            }
                            else
                            {
                                CosmosString tokenAsString = (CosmosString)token;
                                state = ReadFeedState.Continuation(CosmosElement.Parse(tokenAsString.Value));
                            }

                            FeedRangeState<ReadFeedState> feedRangeState = new FeedRangeState<ReadFeedState>(feedRange, state);
                            feedRangeStates.Add(ReadFeedFeedRangeStateSerializer.ToCosmosElement(feedRangeState));
                        }
                    }

                    CosmosArray cosmosArrayContinuationTokens = CosmosArray.Create(feedRangeStates);
                    continuationToken = cosmosArrayContinuationTokens.ToString();
                }
            }

            TryCatch<ReadFeedCrossFeedRangeState> monadicReadFeedState;
            if (continuationToken == null)
            {
                FeedRange feedRange;
                if ((this.queryRequestOptions != null) && this.queryRequestOptions.PartitionKey.HasValue)
                {
                    feedRange = new FeedRangeLogicalPartitionKey(this.queryRequestOptions.PartitionKey.Value);
                }
                else if ((this.queryRequestOptions != null) && (this.queryRequestOptions.FeedRange != null))
                {
                    feedRange = this.queryRequestOptions.FeedRange;
                }
                else
                {
                    feedRange = FeedRangeEpkRange.FullRange;
                }

                monadicReadFeedState = TryCatch<ReadFeedCrossFeedRangeState>.FromResult(ReadFeedCrossFeedRangeState.CreateFromBeginning(feedRange));
            }
            else
            {
                monadicReadFeedState = ReadFeedCrossFeedRangeState.Monadic.Parse(continuationToken);
            }

            if (monadicReadFeedState.Failed)
            {
                this.monadicEnumerator = TryCatch<CrossPartitionReadFeedAsyncEnumerator>.FromException(monadicReadFeedState.Exception);
            }
            else
            {
                this.monadicEnumerator = TryCatch<CrossPartitionReadFeedAsyncEnumerator>.FromResult(
                    CrossPartitionReadFeedAsyncEnumerator.Create(
                        documentContainer,
                        new CrossFeedRangeState<ReadFeedState>(monadicReadFeedState.Result.FeedRangeStates),
                        readFeedPaginationOptions,
                        cancellationToken));
            }

            this.hasMoreResults = true;
        }

        public override bool HasMoreResults => this.hasMoreResults;

        /// <summary>
        /// Get the next set of results from the cosmos service
        /// </summary>
        /// <param name="cancellationToken">(Optional) <see cref="CancellationToken"/> representing request cancellation.</param>
        /// <returns>A query response from cosmos service</returns>
        public override Task<ResponseMessage> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            return this.ReadNextAsync(NoOpTrace.Singleton, cancellationToken);
        }

        public override async Task<ResponseMessage> ReadNextAsync(
            ITrace trace,
            CancellationToken cancellationToken = default)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!this.hasMoreResults)
            {
                throw new InvalidOperationException("Should not be calling FeedIterator that does not have any more results");
            }

            if (this.monadicEnumerator.Failed)
            {
                this.hasMoreResults = false;

                CosmosException cosmosException = ExceptionToCosmosException.CreateFromException(this.monadicEnumerator.Exception);
                return new ResponseMessage(
                    statusCode: System.Net.HttpStatusCode.BadRequest,
                    requestMessage: null,
                    headers: cosmosException.Headers,
                    cosmosException: cosmosException,
                    trace: trace);
            }

            CrossPartitionReadFeedAsyncEnumerator enumerator = this.monadicEnumerator.Result;
            TryCatch<CrossFeedRangePage<Pagination.ReadFeedPage, ReadFeedState>> monadicPage;
            try
            {
                if (!await enumerator.MoveNextAsync(trace))
                {
                    throw new InvalidOperationException("Should not be calling enumerator that does not have any more results");
                }

                monadicPage = enumerator.Current;
            }
            catch (Exception ex)
            {
                monadicPage = TryCatch<CrossFeedRangePage<Pagination.ReadFeedPage, ReadFeedState>>.FromException(ex);
            }

            if (monadicPage.Failed)
            {
                CosmosException cosmosException = ExceptionToCosmosException.CreateFromException(monadicPage.Exception);
                if (!IsRetriableException(cosmosException))
                {
                    this.hasMoreResults = false;
                }

                return new ResponseMessage(
                    statusCode: cosmosException.StatusCode,
                    requestMessage: null,
                    headers: cosmosException.Headers,
                    cosmosException: cosmosException,
                    trace: trace);
            }

            CrossFeedRangePage<Pagination.ReadFeedPage, ReadFeedState> crossFeedRangePage = monadicPage.Result;
            if (crossFeedRangePage.State == default)
            {
                this.hasMoreResults = false;
            }

            // Make the continuation token match the older format:
            string continuationToken;
            if (crossFeedRangePage.State != null)
            {
                List<CompositeContinuationToken> compositeContinuationTokens = new List<CompositeContinuationToken>();
                CrossFeedRangeState<ReadFeedState> crossFeedRangeState = crossFeedRangePage.State;
                for (int i = 0; i < crossFeedRangeState.Value.Length; i++)
                {
                    FeedRangeState<ReadFeedState> feedRangeState = crossFeedRangeState.Value.Span[i];
                    FeedRangeEpkRange feedRange;
                    if (feedRangeState.FeedRange is FeedRangeEpkRange feedRangeEpk)
                    {
                        feedRange = feedRangeEpk;
                    }
                    else
                    {
                        feedRange = FeedRangeEpkRange.FullRange;
                    }

                    ReadFeedState readFeedState = feedRangeState.State;
                    CompositeContinuationToken compositeContinuationToken = new CompositeContinuationToken()
                    {
                        Range = feedRange.Range,
                        Token = readFeedState is ReadFeedBeginningState ? null : ((ReadFeedContinuationState)readFeedState).ContinuationToken.ToString(),
                    };

                    compositeContinuationTokens.Add(compositeContinuationToken);
                }

                FeedRangeInternal outerFeedRange;
                if ((this.queryRequestOptions != null) && this.queryRequestOptions.PartitionKey.HasValue)
                {
                    outerFeedRange = new FeedRangeLogicalPartitionKey(this.queryRequestOptions.PartitionKey.Value);
                }
                else if ((this.queryRequestOptions != null) && (this.queryRequestOptions.FeedRange != null))
                {
                    outerFeedRange = (FeedRangeInternal)this.queryRequestOptions.FeedRange;
                }
                else
                {
                    outerFeedRange = FeedRangeEpkRange.FullRange;
                }

                FeedRangeCompositeContinuation feedRangeCompositeContinuation = new FeedRangeCompositeContinuation(
                    containerRid: string.Empty,
                    feedRange: outerFeedRange,
                    compositeContinuationTokens);

                continuationToken = feedRangeCompositeContinuation.ToString();
            }
            else
            {
                continuationToken = null;
            }

            Pagination.ReadFeedPage page = crossFeedRangePage.Page;
            Headers headers = new Headers()
            {
                RequestCharge = page.RequestCharge,
                ActivityId = page.ActivityId,
                ContinuationToken = continuationToken,
            };
            foreach (KeyValuePair<string, string> kvp in page.AdditionalHeaders)
            {
                headers[kvp.Key] = kvp.Value;
            }

            return new ResponseMessage(
                statusCode: System.Net.HttpStatusCode.OK,
                requestMessage: default,
                headers: headers,
                cosmosException: default,
                trace: trace)
            {
                Content = page.Content,
            };
        }

        public override CosmosElement GetCosmosElementContinuationToken()
        {
            throw new NotSupportedException();
        }
    }
}
