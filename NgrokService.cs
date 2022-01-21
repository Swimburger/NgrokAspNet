using System.Text.Json.Nodes;
using CliWrap;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;
using HttpMethod = Twilio.Http.HttpMethod;

namespace NgrokAspNet;

public class NgrokService : BackgroundService
{
    private readonly IConfiguration config;
    private readonly IHostApplicationLifetime hostApplicationLifetime;
    private readonly ILogger<NgrokService> logger;
    private readonly IServer server;

    public NgrokService(
        IServer server,
        IHostApplicationLifetime hostApplicationLifetime,
        IConfiguration config,
        ILogger<NgrokService> logger
    )
    {
        this.server = server;
        this.hostApplicationLifetime = hostApplicationLifetime;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var completionSource = new TaskCompletionSource();
        hostApplicationLifetime.ApplicationStarted.Register(() => completionSource.TrySetResult());
        await completionSource.Task;

        var urls = server.Features.Get<IServerAddressesFeature>()!.Addresses;
        var httpsUrl = urls.Single(u => u.StartsWith("https://"));

        logger.LogInformation("Starting ngrok tunnel for {HttpsUrl}", httpsUrl);
        var ngrokTask = StartNgrokTunnel(stoppingToken, httpsUrl);

        var publicUrl = await GetNgrokPublicUrl();

        logger.LogInformation("Configuring Webhook URL with {NgrokPublicUrl}", publicUrl);
        await ConfigureTwilioWebhook(publicUrl);

        await ngrokTask;
        logger.LogInformation("Ngrok tunnel stopped");
    }

    private async Task<string> GetNgrokPublicUrl()
    {
        using var httpClient = new HttpClient();
        for (var ngrokRetryCount = 0; ngrokRetryCount < 10; ngrokRetryCount++)
        {
            logger.LogInformation("Ngrok try: {RetryCount}", ngrokRetryCount + 1);

            try
            {
                var json = await httpClient.GetFromJsonAsync<JsonNode>("http://127.0.0.1:4040/api/tunnels");
                var publicUrl = json["tunnels"].AsArray()
                    .Select(e => e["public_url"].GetValue<string>())
                    .SingleOrDefault(u => u.StartsWith("https://"));
                if (!string.IsNullOrEmpty(publicUrl)) return publicUrl;
            }
            catch
            {
                // ignored
            }

            await Task.Delay(200);
        }

        throw new Exception("Ngrok dashboard did not start in 10 tries");
    }

    private CommandTask<CommandResult> StartNgrokTunnel(CancellationToken stoppingToken, string httpsUrl)
    {
        var ngrokTask = Cli.Wrap("ngrok")
            .WithArguments(args => args
                .Add("http")
                .Add(httpsUrl)
                .Add("--log")
                .Add("stdout"))
            .WithStandardOutputPipe(PipeTarget.ToDelegate(s => logger.LogInformation(s)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(s => logger.LogError(s)))
            .ExecuteAsync(stoppingToken);
        return ngrokTask;
    }

    private async Task ConfigureTwilioWebhook(string publicUrl)
    {
        TwilioClient.Init(config["TwilioAccountSid"], config["TwilioAuthToken"]);
        var phoneNumber = (await IncomingPhoneNumberResource.ReadAsync(
            phoneNumber: new PhoneNumber(config["TwilioPhoneNumber"]),
            limit: 1
        )).Single();
        await IncomingPhoneNumberResource.UpdateAsync(
            phoneNumber.Sid,
            voiceUrl: new Uri($"{publicUrl}/voice"), voiceMethod: HttpMethod.Post,
            smsUrl: new Uri($"{publicUrl}/message"), smsMethod: HttpMethod.Post
        );
    }
}