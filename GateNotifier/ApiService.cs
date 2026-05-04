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

    public string? ApiCurrentGateName { get; private set; }
    public int? ApiCurrentGateSlot { get; private set; }
    public DateTime? ApiCurrentGateExpiresAt { get; private set; }

    public string? ApiNextGateName { get; private set; }
    public int? ApiNextGateSlot { get; private set; }
    public DateTime? ApiNextGateExpiresAt { get; private set; }

    public ApiService(Configuration configuration, IPluginLog log)
    {
        this.configuration = configuration;
        this.log = log;
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void ReportGate(string world, string gateName, int slot, string source, string? rawText = null,
        int? gateTypeByte = null, int? positionType = null, int? flags = null, string? course = null,
        string? pluginVersion = null, byte? cycleCounter = null)
    {
        if (!configuration.EnableApiSharing || string.IsNullOrWhiteSpace(configuration.ApiUrl))
            return;

        var body = JsonSerializer.Serialize(new
        {
            world,
            gate = gateName,
            slot,
            source,
            raw_text = rawText,
            gate_type_byte = gateTypeByte,
            position_type = positionType,
            flags,
            course,
            plugin_version = pluginVersion,
            cycle_counter = cycleCounter.HasValue ? (int?)cycleCounter.Value : null,
        });
        var url = configuration.ApiUrl.TrimEnd('/') + "/gate";
        PostWithRetry(url, body, "/gate");
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
                    ClearApiState();
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                log.Debug($"API GET response: {json}");
                var results = JsonSerializer.Deserialize<ApiGateResponse[]>(json);
                if (results == null || results.Length == 0)
                {
                    log.Debug("API GET: no results after deserialize");
                    ClearApiState();
                    return;
                }

                var currentSlot = (DateTime.UtcNow.Minute / 20) * 20;
                log.Debug($"API GET: {results.Length} result(s), currentSlot={currentSlot}");

                ApiCurrentGateName = null;
                ApiCurrentGateSlot = null;
                ApiCurrentGateExpiresAt = null;
                ApiNextGateName = null;
                ApiNextGateSlot = null;
                ApiNextGateExpiresAt = null;

                foreach (var r in results)
                {
                    log.Debug($"API GET: gate={r.Gate} slot={r.Slot}");
                    if (r.Slot == currentSlot)
                    {
                        ApiCurrentGateName = r.Gate;
                        ApiCurrentGateSlot = r.Slot;
                        ApiCurrentGateExpiresAt = r.ExpiresAt;
                    }
                    else
                    {
                        ApiNextGateName = r.Gate;
                        ApiNextGateSlot = r.Slot;
                        ApiNextGateExpiresAt = r.ExpiresAt;
                    }
                }
                if (ApiNextGateName != null)
                    log.Information($"[GateNotifier] API received next GATE: {ApiNextGateName} (slot {ApiNextGateSlot})");
                if (ApiCurrentGateName != null)
                    log.Information($"[GateNotifier] API received current GATE: {ApiCurrentGateName} (slot {ApiCurrentGateSlot})");
            }
            catch (Exception ex)
            {
                log.Debug($"API GET failed: {ex.Message}");
            }
        });
    }

    public void ReportEvent(string world, string eventType, string message,
        string? pluginVersion = null, byte? cycleCounter = null)
    {
        if (!configuration.EnableApiSharing || string.IsNullOrWhiteSpace(configuration.ApiUrl))
            return;

        Task.Run(async () =>
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    world,
                    eventType,
                    message,
                    plugin_version = pluginVersion,
                    cycle_counter = cycleCounter.HasValue ? (int?)cycleCounter.Value : null,
                });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var url = configuration.ApiUrl.TrimEnd('/') + "/event";
                var response = await httpClient.PostAsync(url, content);
                log.Debug($"API POST /event: {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                log.Debug($"API POST /event failed: {ex.Message}");
            }
        });
    }

    private void PostWithRetry(string url, string body, string label, int maxRetries = 3)
    {
        Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var content = new StringContent(body, Encoding.UTF8, "application/json");
                    log.Information($"API POST {label}: attempt {attempt}/{maxRetries}");
                    var response = await httpClient.PostAsync(url, content);
                    log.Information($"API POST {label}: {(int)response.StatusCode}");
                    return;
                }
                catch (Exception ex)
                {
                    log.Warning($"API POST {label} attempt {attempt}/{maxRetries} failed: {ex.GetType().Name}: {ex.Message}");
                    if (attempt < maxRetries)
                        await Task.Delay(5000);
                }
            }
            log.Error($"API POST {label}: all {maxRetries} attempts failed, data lost");
        });
    }

    public void SetSimulatedNextGate(string gateName, int slot, DateTime expiresAt)
    {
        ApiNextGateName = gateName;
        ApiNextGateSlot = slot;
        ApiNextGateExpiresAt = expiresAt;
    }

    public void ClearApiState()
    {
        ApiCurrentGateName = null;
        ApiCurrentGateSlot = null;
        ApiCurrentGateExpiresAt = null;
        ApiNextGateName = null;
        ApiNextGateSlot = null;
        ApiNextGateExpiresAt = null;
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
