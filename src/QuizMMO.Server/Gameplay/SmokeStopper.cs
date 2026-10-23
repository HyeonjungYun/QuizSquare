using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuizMMO.Server.Core;

namespace QuizMMO.Server.Gameplay;

/// <summary>Smoke:Seconds > 0 이면 그 시간 뒤 호스트를 정상 종료시킨다 (기동 확인·자동 테스트용).</summary>
public sealed class SmokeStopper : BackgroundService
{
    private readonly int _seconds;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SmokeStopper> _logger;

    public SmokeStopper(IOptions<SmokeOptions> options, IHostApplicationLifetime lifetime, ILogger<SmokeStopper> logger)
    {
        _seconds = options.Value.Seconds;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_seconds <= 0) return;
        _logger.LogInformation("smoke mode: stopping in {Seconds} seconds", _seconds);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_seconds), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _lifetime.StopApplication();
    }
}
