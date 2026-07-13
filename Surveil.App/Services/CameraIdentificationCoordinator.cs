using Surveil.App.ViewModels;
using Surveil.Core;

namespace Surveil.App.Services;

/// <summary>Owns bounded, cancellable camera enrichment independently of discovery and scan commands.</summary>
public sealed class CameraIdentificationCoordinator(SurveilService service, int maxConcurrency = 6)
{
    public async Task IdentifyAsync(IReadOnlyList<CameraItem> cameras, string username, string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            foreach (var camera in cameras) camera.LoginState = CameraLoginState.NoCredentials;
            return;
        }

        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var work = cameras.Select(async camera =>
        {
            await gate.WaitAsync(cancellationToken);
            camera.LoginState = CameraLoginState.InProgress;
            try
            {
                var endpoint = camera.Endpoint ?? new UriBuilder("http", camera.Ip)
                { Path = "/onvif/device_service" }.Uri;
                camera.ApplyFeatures(await service.IdentifyAsync(endpoint, username, password, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                camera.LoginState = CameraLoginState.NotTried;
            }
            catch (OnvifException error) when (error.IsAuthenticationFailure)
            {
                camera.LoginState = CameraLoginState.AuthFailed;
                camera.ErrorText = error.Message;
            }
            catch (Exception error)
            {
                camera.LoginState = CameraLoginState.Unreachable;
                camera.ErrorText = error.Message;
                AppLog.Write(error);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(work);
    }
}
