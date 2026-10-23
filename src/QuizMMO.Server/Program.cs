// QuizMMO.Server — Quiz MMO 전용 서버 진입점 (.NET Generic Host).
//
// 설정은 appsettings.json 의 "Server" 섹션이고, 커맨드라인으로 덮어쓸 수 있다:
//   dotnet run --project src/QuizMMO.Server -- --Server:Quiz:RoomDurationSec=3 --Smoke:Seconds=10
// Smoke:Seconds > 0 이면 그 시간 뒤 스스로 종료한다 (기동 확인용). Ctrl+C 는 Host 가 처리해 StopAsync 로 이어진다.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuizMMO.Server.Core;
using QuizMMO.Server.Gameplay;

var builder = Host.CreateApplicationBuilder(args);

// 기본 설정은 현재 디렉터리의 appsettings.json 을 읽는다. 어느 디렉터리에서 실행해도 실행 파일 옆의 파일을 찾도록
// 보조 소스를 추가하고, 커맨드라인이 그보다 우선하도록 다시 등록한다.
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddCommandLine(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
});

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
builder.Services.Configure<SmokeOptions>(builder.Configuration.GetSection(SmokeOptions.SectionName));
builder.Services.AddHostedService<GameServer>();
builder.Services.AddHostedService<SmokeStopper>();

await builder.Build().RunAsync();
