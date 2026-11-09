using TrueMoon;
using TrueMoon.Tales.Studio.Configuration;
using TrueMoon.Tales.Studio.Infrastructure;
using TrueMoon.Tales.Studio.Presentation;

await App.Builder(_ => { })
    .Setup(app => app
        .UseStudioServices()
        .UseStudioWindow())
    .RunStudioAsync(args);
