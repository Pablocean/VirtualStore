using System.Runtime.CompilerServices;

// Wave T0: exposes internal testability seams (Stripe transient check,
// MongoDbContext session setter) to the unit-test assembly.
[assembly: InternalsVisibleTo("VirtualStore.UnitTests")]
