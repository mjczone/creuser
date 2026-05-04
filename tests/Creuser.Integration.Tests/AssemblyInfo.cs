// Disable parallel test execution at the assembly level.
// Each integration test class spins up its own PostgresFixture
// (Testcontainers) + CreuserApiFactory (WebApplicationFactory<Program>),
// and Wolverine + Marten share runtime-generated code via JasperFx that
// doesn't handle multiple concurrent stores against different connection
// strings cleanly. Serial execution within the assembly avoids the
// cross-class cache contention without losing the per-class isolation
// PostgresFixture provides. Different test classes still each get a
// fresh container; they just don't run simultaneously.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
