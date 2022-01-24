using System.Text.Json.Nodes;
using CliWrap;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Twilio.Clients;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;
using HttpMethod = Twilio.Http.HttpMethod;

namespace NgrokAspNet;

public class TunnelService : BackgroundService
{
    private readonly IConfiguration config;
    private readonly IHostApplicationLifetime hostApplicationLifetime;
    private readonly ILogger<TunnelService> logger;
    private readonly IServer server;

    public TunnelService(
        IServer server,
        IHostApplicationLifetime hostApplicationLifetime,
        IConfiguration config,
        ILogger<TunnelService> logger
    )
    {
        this.server = server;
        this.hostApplicationLifetime = hostApplicationLifetime;
        this.config = config;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForApplicationStarted();

        var urls = server.Features.Get<IServerAddressesFeature>()!.Addresses;
        // Use https:// if you authenticated ngrok, otherwise, you can only use http://
        var localUrl = urls.Single(u => u.StartsWith("http://"));

        logger.LogInformation("Starting ngrok tunnel for {LocalUrl}", localUrl);
        var ngrokTask = StartNgrokTunnel(localUrl, stoppingToken);

        var publicUrl = await GetNgrokPublicUrl();
        logger.LogInformation("Public ngrok URL: {NgrokPublicUrl}", publicUrl);

        await ConfigureTwilioWebhook(publicUrl);

        await ngrokTask;

        logger.LogInformation("Ngrok tunnel stopped");
    }

    private Task WaitForApplicationStarted()
    {
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostApplicationLifetime.ApplicationStarted.Register(() => completionSource.TrySetResult());
        return completionSource.Task;
    }

    private CommandTask<CommandResult> StartNgrokTunnel(string localUrl, CancellationToken stoppingToken)
    {
        var ngrokTask = Cli.Wrap("ngrok")
            .WithArguments(args => args
                .Add("http")
                .Add(localUrl)
                .Add("--log")
                .Add("stdout"))
            .WithStandardOutputPipe(PipeTarget.ToDelegate(s => logger.LogDebug(s)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(s => logger.LogError(s)))
            .ExecuteAsync(stoppingToken);
        return ngrokTask;
    }

    private async Task<string> GetNgrokPublicUrl()
    {
        using var httpClient = new HttpClient();
        for (var ngrokRetryCount = 0; ngrokRetryCount < 10; ngrokRetryCount++)
        {
            logger.LogDebug("Get ngrok tunnels attempt: {RetryCount}", ngrokRetryCount + 1);

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

    private async Task ConfigureTwilioWebhook(string publicUrl)
    {
        var twilioClient = new TwilioRestClient(config["TwilioAccountSid"], config["TwilioAuthToken"]);
        var phoneNumber = (await IncomingPhoneNumberResource.ReadAsync(
            phoneNumber: new PhoneNumber(config["TwilioPhoneNumber"]),
            limit: 1,
            client: twilioClient
        )).Single();
        phoneNumber = await IncomingPhoneNumberResource.UpdateAsync(
            phoneNumber.Sid,
            voiceUrl: new Uri($"{publicUrl}/voice"), voiceMethod: HttpMethod.Post,
            smsUrl: new Uri($"{publicUrl}/message"), smsMethod: HttpMethod.Post,
            client: twilioClient
        );
        logger.LogInformation(
            "Twilio Phone Number {TwilioPhoneNumber} Voice URL updated to {TwilioVoiceUrl}",
            phoneNumber.PhoneNumber,
            phoneNumber.VoiceUrl
        );
        logger.LogInformation(
            "Twilio Phone Number {TwilioPhoneNumber} Message URL updated to {TwilioMessageUrl}",
            phoneNumber.PhoneNumber,
            phoneNumber.SmsUrl
        );
    }
}