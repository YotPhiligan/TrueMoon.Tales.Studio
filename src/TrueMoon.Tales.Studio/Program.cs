using TrueMoon;
using TrueMoon.Tales.Studio.Backend;
using TrueMoon.Tales.Studio.Backend.Export;
using TrueMoon.Tales.Studio.Backend.Http;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Presentation;

if (args.FirstOrDefault() == BackendExportWorker.Command)
    return await BackendExportWorker.RunChildAsync(args);

if (args.FirstOrDefault() == BackendFullTransferWorker.Command)
    return await BackendFullTransferWorker.RunChildAsync(args);

if (BackendHttpCli.Handles(args.FirstOrDefault()))
    return await BackendHttpCli.RunAsync(args);

await StudioReviewSession.CleanAbandonedAsync(StudioReviewSession.DefaultRoot);
await StudioReviewSession.CleanAbandonedAsync(StudioReviewSession.FullTransferRoot, preserveCompleted: true);
var builder = App.Builder(_ => { });
await using var preferences = StudioOptions.TryParse(args, out _) ? StudioUiPreferences.OpenDefault() : null;
if (preferences is not null)
    builder.Setup(context => context.UseStudioServices(preferences).UseStudioWindow(preferences));
return await builder.RunStudioAsync(args);
