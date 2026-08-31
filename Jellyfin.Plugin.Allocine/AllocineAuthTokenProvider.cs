using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Allocine
{
    internal sealed class AllocineAuthTokenProvider : IDisposable
    {
        private const string CheckinUrl = "https://android.clients.google.com/checkin";
        private const string RegisterUrl = "https://android.clients.google.com/c2dm/register3";
        private const string PackageName = "com.allocine.androidapp";
        private const string SenderId = "848548993493";
        private const string ApplicationId = "1:848548993493:android:6e48a26431174bc5";
        private const string CertificateSha1 = "b708782e3014076a78bdd85b60f77fa797f7a021";
        private const string ApplicationVersion = "553";
        private const string ApplicationVersionName = "9.10.18";
        private const string AndroidVersion = "35";

        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private volatile string? _cachedToken;

        public AllocineAuthTokenProvider(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public void Dispose()
        {
            _refreshLock.Dispose();
        }

        public async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!forceRefresh && _cachedToken != null)
            {
                return _cachedToken;
            }

            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!forceRefresh && _cachedToken != null)
                {
                    return _cachedToken;
                }

                _cachedToken = await RegisterAsync(cancellationToken).ConfigureAwait(false);
                return _cachedToken;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<string> RegisterAsync(CancellationToken cancellationToken)
        {
            GoogleCheckinCredentials credentials = await CheckinAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, RegisterUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "AidLogin",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{credentials.AndroidId}:{credentials.SecurityToken}"));
            request.Headers.UserAgent.ParseAdd("Android-GCM/1.5");
            request.Content = new FormUrlEncodedContent(CreateRegistrationFields(credentials));

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            const string tokenPrefix = "token=";
            foreach (string line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith(tokenPrefix, StringComparison.Ordinal) || line.Length == tokenPrefix.Length)
                {
                    continue;
                }

                string token = Uri.UnescapeDataString(line[tokenPrefix.Length..]);
                if (!string.IsNullOrWhiteSpace(token) && !ContainsControlCharacter(token))
                {
                    return token;
                }
            }

            throw new InvalidOperationException("Anonymous FCM registration did not return a token.");
        }

        private async Task<GoogleCheckinCredentials> CheckinAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CheckinUrl);
            request.Headers.UserAgent.ParseAdd("Android-GCM/1.5");
            request.Content = new ByteArrayContent(GoogleCheckinCodec.CreateRequest());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return GoogleCheckinCodec.ParseResponse(content);
        }

        private static bool ContainsControlCharacter(string value)
        {
            foreach (char character in value)
            {
                if (char.IsControl(character))
                {
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<string, string> CreateRegistrationFields(GoogleCheckinCredentials credentials)
        {
            string androidId = credentials.AndroidId.ToString(CultureInfo.InvariantCulture);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app"] = PackageName,
                ["X-subtype"] = SenderId,
                ["device"] = androidId,
                ["sender"] = SenderId,
                ["cert"] = CertificateSha1,
                ["app_ver"] = ApplicationVersion,
                ["X-app_ver"] = ApplicationVersion,
                ["app_ver_name"] = ApplicationVersionName,
                ["X-app_ver_name"] = ApplicationVersionName,
                ["osv"] = AndroidVersion,
                ["X-osv"] = AndroidVersion,
                ["X-gmsv"] = "250000000",
                ["X-cliv"] = "fiid-21.1.1",
                ["scope"] = "*",
                ["X-scope"] = "*",
                ["target_ver"] = AndroidVersion,
                ["X-gms_app_id"] = ApplicationId,
            };
        }
    }
}
