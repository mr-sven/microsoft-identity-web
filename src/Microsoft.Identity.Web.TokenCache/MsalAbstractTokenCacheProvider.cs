﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace Microsoft.Identity.Web.TokenCacheProviders
{
    /// <summary>
    /// Token cache provider with default implementation.
    /// </summary>
    /// <seealso cref="Microsoft.Identity.Web.TokenCacheProviders.IMsalTokenCacheProvider" />
    public abstract partial class MsalAbstractTokenCacheProvider : IMsalTokenCacheProvider
    {
        private readonly IDataProtector? _protector;
        private readonly ILogger<MsalAbstractTokenCacheProvider>? _logger;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="dataProtector">Service provider. Can be null, in which case the token cache
        /// will not be encrypted. See https://aka.ms/ms-id-web/token-cache-encryption.</param>
        protected MsalAbstractTokenCacheProvider(IDataProtector? dataProtector = null)
        {
            _protector = dataProtector;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="dataProtector">Service provider. Can be null, in which case the token cache
        /// will not be encrypted. See https://aka.ms/ms-id-web/token-cache-encryption.</param>
        /// <param name="logger">MsalDistributedTokenCacheAdapter logger.</param>
        protected MsalAbstractTokenCacheProvider(IDataProtector? dataProtector, ILogger<MsalAbstractTokenCacheProvider> logger)
        {
            _protector = dataProtector;
            _logger = logger;
        }

        /// <summary>
        /// Initializes the token cache serialization.
        /// </summary>
        /// <param name="tokenCache">Token cache to serialize/deserialize.</param>
        public void Initialize(ITokenCache tokenCache)
        {
            if (tokenCache == null)
            {
                throw new ArgumentNullException(nameof(tokenCache));
            }

            tokenCache.SetBeforeAccessAsync(OnBeforeAccessAsync);
            tokenCache.SetAfterAccessAsync(OnAfterAccessAsync);
            tokenCache.SetBeforeWriteAsync(OnBeforeWriteAsync);
        }

        /// <summary>
        /// Initializes the token cache serialization.
        /// </summary>
        /// <param name="tokenCache">Token cache to serialize/deserialize.</param>
        /// <returns>A <see cref="Task"/> that represents a completed initialization operation.</returns>
        public Task InitializeAsync(ITokenCache tokenCache)
        {
            Initialize(tokenCache);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Raised AFTER MSAL added the new token in its in-memory copy of the cache.
        /// This notification is called every time MSAL accesses the cache, not just when a write takes place:
        /// If MSAL's current operation resulted in a cache change, the property TokenCacheNotificationArgs.HasStateChanged will be set to true.
        /// If that is the case, we call the TokenCache.SerializeMsalV3() to get a binary blob representing the latest cache content – and persist it.
        /// </summary>
        /// <param name="args">Contains parameters used by the MSAL call accessing the cache.</param>
        private async Task OnAfterAccessAsync(TokenCacheNotificationArgs args)
        {
            // The access operation resulted in a cache update.
            if (args.HasStateChanged)
            {
                CacheSerializerHints cacheSerializerHints = CreateHintsFromArgs(args);

                if (args.HasTokens)
                {
                    await WriteCacheBytesAsync(args.SuggestedCacheKey, ProtectBytes(args.TokenCache.SerializeMsalV3()), cacheSerializerHints).ConfigureAwait(false);
                }
                else
                {
                    // No token in the cache. we can remove the cache entry
                    await RemoveKeyAsync(args.SuggestedCacheKey, cacheSerializerHints).ConfigureAwait(false);
                }
            }
        }

        private byte[] ProtectBytes(byte[] msalBytes)
        {
            if (msalBytes != null && _protector != null)
            {
                return _protector.Protect(msalBytes);
            }

            return msalBytes!;
        }

        private static CacheSerializerHints CreateHintsFromArgs(TokenCacheNotificationArgs args) => new CacheSerializerHints { CancellationToken = args.CancellationToken, SuggestedCacheExpiry = args.SuggestedCacheExpiry };

        private async Task OnBeforeAccessAsync(TokenCacheNotificationArgs args)
        {
            if (!string.IsNullOrEmpty(args.SuggestedCacheKey))
            {
                byte[] tokenCacheBytes = await ReadCacheBytesAsync(args.SuggestedCacheKey, CreateHintsFromArgs(args)).ConfigureAwait(false);

                try
                {
                    args.TokenCache.DeserializeMsalV3(UnprotectBytes(tokenCacheBytes), shouldClearExistingCache: true);
                }
                catch (MsalClientException exception)
                {
                    if (_logger != null)
                    {
                        Logger.CacheDeserializationError(
                          _logger,
                          args.SuggestedCacheKey,
                          _protector != null,
                          exception.Message,
                          exception);
                    }
                    // Adding a better message specifically for JSON parsing error
                    if (exception.ErrorCode == MsalError.JsonParseError)
                    {
                        throw new MsalClientException(MsalError.JsonParseError, TokenCacheErrorMessage.ExceptionDeserializingCache, exception);
                    }
                    throw;
                }
            }
        }

        private byte[] UnprotectBytes(byte[] msalBytes)
        {
            if (msalBytes != null && _protector != null)
            {
                try
                {
                    return _protector.Unprotect(msalBytes);
                }
                catch (CryptographicException)
                {
                    // Also handles case of previously unencrypted cache
                    return msalBytes;
                }
            }

            return msalBytes!;
        }

        /// <summary>
        /// If you want to ensure that no concurrent write takes place, use this notification to place a lock on the entry.
        /// </summary>
        /// <param name="args">Token cache notification arguments.</param>
        /// <returns>A <see cref="Task"/> that represents a completed operation.</returns>
        protected virtual Task OnBeforeWriteAsync(TokenCacheNotificationArgs args)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Clear the cache.
        /// </summary>
        /// <param name="homeAccountId">HomeAccountId for a user account in the cache.</param>
        /// <returns>A <see cref="Task"/> that represents a completed clear operation.</returns>
        public async Task ClearAsync(string homeAccountId)
        {
            // This is a user token cache
            await RemoveKeyAsync(homeAccountId).ConfigureAwait(false);

            // TODO: Clear the cookie session if any. Get inspiration from
            // https://github.com/Azure-Samples/active-directory-aspnetcore-webapp-openidconnect-v2/issues/240
        }

        /// <summary>
        /// Method to be implemented by concrete cache serializers to write the cache bytes.
        /// </summary>
        /// <param name="cacheKey">Cache key.</param>
        /// <param name="bytes">Bytes to write.</param>
        /// <returns>A <see cref="Task"/> that represents a completed write operation.</returns>
        protected abstract Task WriteCacheBytesAsync(string cacheKey, byte[] bytes);

        /// <summary>
        /// Method to be overridden by concrete cache serializers to write the cache bytes.
        /// </summary>
        /// <param name="cacheKey">Cache key.</param>
        /// <param name="bytes">Bytes to write.</param>
        /// <param name="cacheSerializerHints">Hints for the cache serialization implementation optimization.</param>
        /// <returns>A <see cref="Task"/> that represents a completed write operation.</returns>
        protected virtual Task WriteCacheBytesAsync(string cacheKey, byte[] bytes, CacheSerializerHints cacheSerializerHints)
        {
            return WriteCacheBytesAsync(cacheKey, bytes); // default implementation avoids a breaking change.
        }

        /// <summary>
        /// Method to be implemented by concrete cache serializers to Read the cache bytes.
        /// </summary>
        /// <param name="cacheKey">Cache key.</param>
        /// <returns>Read bytes.</returns>
        protected abstract Task<byte[]> ReadCacheBytesAsync(string cacheKey);

        /// <summary>
        /// Method to be overridden by concrete cache serializers to Read the cache bytes.
        /// </summary>
        /// <param name="cacheKey">Cache key.</param>
        /// <param name="cacheSerializerHints">Hints for the cache serialization implementation optimization.</param>
        /// <returns>Read bytes.</returns>
        protected virtual Task<byte[]> ReadCacheBytesAsync(string cacheKey, CacheSerializerHints cacheSerializerHints)
        {
            return ReadCacheBytesAsync(cacheKey); // default implementation avoids a breaking change.
        }

        /// <summary>
        /// Method to be implemented by concrete cache serializers to remove an entry from the cache.
        /// </summary>
        /// <param name="cacheKey">Cache key.</param>
        /// <returns>A <see cref="Task"/> that represents a completed remove key operation.</returns>
        protected abstract Task RemoveKeyAsync(string cacheKey);

        /// <summary>
        /// Method to be overridden by concrete cache serializers to remove an entry from the cache.
        /// </summary>
        /// <param name="cacheKey">Cache key.</param>
        /// <param name="cacheSerializerHints">Hints for the cache serialization implementation optimization.</param>
        /// <returns>A <see cref="Task"/> that represents a completed remove key operation.</returns>
        protected virtual Task RemoveKeyAsync(string cacheKey, CacheSerializerHints cacheSerializerHints)
        {
            return RemoveKeyAsync(cacheKey); // default implementation avoids a breaking change.
        }
    }
}
