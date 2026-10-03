using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Dmdata.Security;

namespace EEWTelop.Infrastructure.Dmdata.Transport;

internal static class DmdataAuthorizedHttpClient
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        IDmdataCredentialProvider credentialProvider,
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = createRequest();
            DmdataCredential credential = await credentialProvider.GetCredentialAsync(cancellationToken)
                .ConfigureAwait(false);
            request.Headers.Authorization = credential.AuthenticationMode switch
            {
                DmdataAuthenticationMode.ApiKey => new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Secret + ":"))),
                DmdataAuthenticationMode.OAuthAccessToken => new AuthenticationHeaderValue("Bearer", credential.Secret),
                _ => throw new InvalidOperationException("Unsupported DMDATA.JP authentication mode."),
            };
            HttpResponseMessage response = await httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt != 0 ||
                credential.AuthenticationMode != DmdataAuthenticationMode.OAuthAccessToken)
                return response;
            bool refreshed;
            try
            {
                refreshed = await credentialProvider.RefreshAfterUnauthorizedAsync(
                    credential.Secret, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }
            if (!refreshed) return response;
            response.Dispose();
        }
    }
}
