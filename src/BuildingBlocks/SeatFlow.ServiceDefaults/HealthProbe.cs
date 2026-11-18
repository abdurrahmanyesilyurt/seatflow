namespace SeatFlow.ServiceDefaults;

/// <summary>
/// The runtime images are chiseled: no shell, no curl. Docker's HEALTHCHECK therefore runs
/// <c>dotnet App.dll healthcheck</c>, which calls the app's own readiness endpoint and exits 0 or 1.
/// </summary>
public static class HealthProbe
{
    public static async Task<int?> RunIfRequestedAsync(string[] args)
    {
        if (args is not ["healthcheck", ..])
        {
            return null;
        }

        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';', ',')[0];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(new Uri($"http://localhost:{port}/health/ready"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
