using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace GateNotifier;

public sealed class ApiService : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private DateTime lastPollTime = DateTime.MinValue;
    private const int PollIntervalSeconds = 30;

    public string? ApiGateName { get; private set; }
    public int? ApiGateSlot { get; private set; }
    public DateTime? ApiGateExpiresAt { get; private set; }

    public ApiService(Configuration configuration, IPluginLog log)
    {
        this.configuration = configuration;
        this.log = log;
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public void ReportGate(string world, string gateName, int slot)
    {
        if (!configuration.EnableApiSharing || string.IsNullOrWhiteSpace(configuration.ApiUrl))
            return;

        // Fire and forget
        Task.Run(async () =>
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    world,
                    gate = gateName,
                    slot,
                });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var url = configuration.ApiUrl.TrimEnd('/') + "/gate";
                var response = await httpClient.PostAsync(url, content);
                log.Debug($"API POST /gate: {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                log.Debug($"API POST failed: {ex.Message}");
            }
        });
    }

    public void PollIfNeeded(string? world)
    {
        if (!configuration.EnableApiSharing || string.IsNullOrWhiteSpace(configuration.ApiUrl))
            return;

        if (string.IsNullOrWhiteSpace(world))
            return;

        var now = DateTime.UtcNow;
        if ((now - lastPollTime).TotalSeconds < PollIntervalSeconds)
            return;

        lastPollTime = now;

        Task.Run(async () =>
        {
            try
            {
                var url = configuration.ApiUrl.TrimEnd('/') + $"/gate/{Uri.EscapeDataString(world)}";
                var response = await httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    ApiGateName = null;
                    ApiGateSlot = null;
                    ApiGateExpiresAt = null;
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ApiGateResponse>(json);
                if (result != null)
                {
                    ApiGateName = result.Gate;
                    ApiGateSlot = result.Slot;
                    ApiGateExpiresAt = result.ExpiresAt;
                }
            }
            catch (Exception ex)
            {
                log.Debug($"API GET failed: {ex.Message}");
            }
        });
    }

    public void ClearApiState()
    {
        ApiGateName = null;
        ApiGateSlot = null;
        ApiGateExpiresAt = null;
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private class ApiGateResponse
    {
        [JsonPropertyName("gate")]
        public string? Gate { get; set; }

        [JsonPropertyName("slot")]
        public int Slot { get; set; }

        [JsonPropertyName("reportedAt")]
        public DateTime ReportedAt { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTime ExpiresAt { get; set; }
    }
}
