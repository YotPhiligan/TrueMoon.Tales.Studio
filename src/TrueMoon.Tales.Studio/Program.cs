using TrueMoon;
using TrueMoon.Tales.Studio.Configuration;

return await App.Builder(_ => { })
    .Setup(context => context.UseStudioUi(args))
    .RunStudioAsync(args);
