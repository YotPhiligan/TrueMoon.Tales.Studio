using TrueMoon;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Presentation;

var builder = App.Builder(_ => { });
await using var preferences = StudioOptions.TryParse(args, out _) ? StudioUiPreferences.OpenDefault() : null;
if (preferences is not null)
    builder.Setup(context => context.UseStudioServices(preferences).UseStudioWindow(preferences));
return await builder.RunStudioAsync(args);
