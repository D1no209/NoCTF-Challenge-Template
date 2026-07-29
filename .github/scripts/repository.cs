#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property EnableAotAnalyzer=false
#:property EnableTrimAnalyzer=false
#:package YamlDotNet@16.3.0
#:include RepositoryApp.Core.cs
#:include RepositoryApp.Validation.cs
#:include RepositoryApp.RuntimeValidation.cs
#:include RepositoryApp.Manifest.cs
#:include RepositoryApp.Utilities.cs
#:include RepositoryApp.Build.cs
#:include RepositoryApp.Scaffold.cs
#:include RepositoryApp.Initialize.cs
#:include RepositoryApp.Readme.cs
#:include RepositoryApp.Apply.cs
#:include RepositoryApp.ApplyResources.cs
#:include RepositoryApp.CompetitionApply.cs
#:include RepositoryApp.Definition.cs
#:include RepositoryApp.ManifestTemplates.cs
#:include NoCtfClient.cs

return await RepositoryApp.RunAsync(args);
