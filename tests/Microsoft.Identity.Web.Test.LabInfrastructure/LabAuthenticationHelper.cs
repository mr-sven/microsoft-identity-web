﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web.Test.Common.TestHelpers;

namespace Microsoft.Identity.Web.Test.LabInfrastructure
{
    public static class LabAuthenticationHelper
    {
        private static readonly InMemoryTokenCache s_staticCache = new InMemoryTokenCache();
        private const string LabAccessConfidentialClientId = "16dab2ba-145d-4b1b-8569-bf4b9aed4dc8";
        private const string LabAccessPublicClientId = "3c1e0e0d-b742-45ba-a35e-01c664e14b16";
        private const string LabAccessThumbPrint = "4E87313FD450985A10BC0F14A292859F2DCD6CD3";
        private const string DataFileName = "data.txt";
        private static readonly LabAccessAuthenticationType s_defaultAuthType = LabAccessAuthenticationType.ClientCertificate;
        private static readonly string s_secret;

        static LabAuthenticationHelper()
        {
            // The data.txt is a place holder for the Key Vault secret. It will only be written to during build time when testing App Center.
            // After the tests are finished in App Center, the file will be deleted from the App Center servers.
            // The file will then be deleted locally Via VSTS task.
            if (File.Exists(DataFileName))
            {
                var data = File.ReadAllText(DataFileName);

                if (!string.IsNullOrWhiteSpace(data))
                {
                    s_defaultAuthType = LabAccessAuthenticationType.ClientSecret;
                    s_secret = data;
                }
            }
        }

        public static async Task<string> GetAccessTokenForLabAPIAsync(string labAccessClientId, string labAccessSecret)
        {
            string[] scopes = new string[] { "https://msidlab.com/.default" };

            return await GetLabAccessTokenAsync(
                "https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/",
                scopes,
                LabAccessAuthenticationType.ClientSecret,
                labAccessClientId,
                string.Empty,
                labAccessSecret).ConfigureAwait(false);
        }

        public static async Task<string> GetLabAccessTokenAsync(string authority, string[] scopes)
        {
            return await GetLabAccessTokenAsync(
                authority,
                scopes,
                s_defaultAuthType,
                string.Empty,
                string.Empty,
                string.Empty).ConfigureAwait(false);
        }

        public static async Task<string> GetLabAccessTokenAsync(string authority, string[] scopes, LabAccessAuthenticationType authType, string clientId, string certThumbprint, string clientSecret)
        {
            AuthenticationResult authResult;
            IConfidentialClientApplication confidentialApp;
            X509Certificate2 cert;

            switch (authType)
            {
                case LabAccessAuthenticationType.ClientCertificate:
                    var clientIdForCertAuth = string.IsNullOrEmpty(clientId) ? LabAccessConfidentialClientId : clientId;
                    var certThumbprintForLab = string.IsNullOrEmpty(clientId) ? LabAccessThumbPrint : certThumbprint;

                    cert = CertificateHelper.FindCertificateByThumbprint(certThumbprintForLab);
                    if (cert == null)
                    {
                        throw new InvalidOperationException(
                            "Test setup error - cannot find a certificate in the My store for KeyVault. This is available for Microsoft employees only.");
                    }

                    confidentialApp = ConfidentialClientApplicationBuilder
                        .Create(clientIdForCertAuth)
                        .WithAuthority(new Uri(authority), true)
                        .WithCertificate(cert)
                        .Build();

                    s_staticCache.Bind(confidentialApp.AppTokenCache);

                    authResult = await confidentialApp
                        .AcquireTokenForClient(scopes)
                        .ExecuteAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case LabAccessAuthenticationType.ClientSecret:
                    var clientIdForSecretAuth = string.IsNullOrEmpty(clientId) ? LabAccessConfidentialClientId : clientId;
                    var clientSecretForLab = string.IsNullOrEmpty(clientId) ? s_secret : clientSecret;

                    confidentialApp = ConfidentialClientApplicationBuilder
                        .Create(clientIdForSecretAuth)
                        .WithAuthority(new Uri(authority), true)
                        .WithClientSecret(clientSecretForLab)
                        .Build();
                    s_staticCache.Bind(confidentialApp.AppTokenCache);

                    authResult = await confidentialApp
                        .AcquireTokenForClient(scopes)
                        .ExecuteAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case LabAccessAuthenticationType.UserCredential:
                    var clientIdForPublicClientAuth = string.IsNullOrEmpty(clientId) ? LabAccessPublicClientId : clientId;
                    var publicApp = PublicClientApplicationBuilder
                        .Create(clientIdForPublicClientAuth)
                        .WithAuthority(new Uri(authority), true)
                        .Build();
                    s_staticCache.Bind(publicApp.UserTokenCache);

                    authResult = await publicApp
                        .AcquireTokenByIntegratedWindowsAuth(scopes)
                        .ExecuteAsync(CancellationToken.None)
                        .ConfigureAwait(false);

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return authResult?.AccessToken;
        }
    }

    public enum LabAccessAuthenticationType
    {
        ClientCertificate,
        ClientSecret,
        UserCredential,
    }
}
