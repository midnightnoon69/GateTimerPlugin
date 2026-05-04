using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace GateAnalyzer;

public sealed class AnalyzerApiService : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly IPluginLog log;
    private readonly AnalyzerConfiguration configuration;

    public AnalyzerApiService(AnalyzerConfiguration configuration, IPluginLog log)
    {
        this.configuration = configuration;
        this.log = log;
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void ReportGate(string world, string gateName, int slot, string source, string? rawText = null,
        int? gateTypeByte = null, int? positionType = null, int? flags = null, string? course = null)
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
        });
        var url = configuration.ApiUrl.TrimEnd('/') + "/gate";
        PostWithRetry(url, body, "/gate");
    }

    public void ReportScan(string world, int slot, string? gate, int? gateTypeByte,
        byte[] gsmBytes, byte[]? gfdBytes, string phase = "active", string? course = null)
    {
        if (!configuration.EnableApiSharing || string.IsNullOrWhiteSpace(configuration.ApiUrl))
            return;

        var body = JsonSerializer.Serialize(new
        {
            world,
            slot,
            phase,
            gate,
            gate_type_byte = gateTypeByte,
            gsm_base64 = Convert.ToBase64String(gsmBytes),
            gfd_base64 = gfdBytes != null ? Convert.ToBase64String(gfdBytes) : null,
            gsm_size = gsmBytes.Length,
            gfd_size = gfdBytes?.Length,
            course,
        });
        var url = configuration.ApiUrl.TrimEnd('/') + "/scan";
        PostWithRetry(url, body, "/scan");
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

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
