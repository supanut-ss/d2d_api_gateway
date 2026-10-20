namespace D2D.Tests.Integration;

/// <summary>One API instance for every integration test class: settings reach the app through process-wide environment variables.</summary>
[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
