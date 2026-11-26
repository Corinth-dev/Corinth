using Xunit;

// Tests listen to the process-wide "Ccf" meter and start hosts on real sockets; run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
