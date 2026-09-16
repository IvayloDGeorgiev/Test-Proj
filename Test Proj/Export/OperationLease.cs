namespace Test_Proj.Export;

/// <summary>Process-wide immediate admission. Services hold a lease across retrieval and export.</summary>
public sealed class OperationLease
{
    private int occupied;
    public Lease Acquire()
    {
        if (Interlocked.CompareExchange(ref occupied, 1, 0) != 0)
            throw new ExportException("operation_busy", 409);
        return new Lease(this);
    }

    public sealed class Lease : IDisposable
    {
        private readonly OperationLease owner;
        private readonly object sync = new();
        private bool disposed;
        private bool writing;
        internal Lease(OperationLease owner) => this.owner = owner;
        internal IDisposable BeginWrite(OperationLease expected)
        {
            lock (sync)
            {
                if (owner != expected || disposed || writing) throw new ExportException("invalid_operation_lease");
                writing = true;
                return new WriteScope(this);
            }
        }
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (!writing) Volatile.Write(ref owner.occupied, 0);
            }
        }
        private sealed class WriteScope(Lease lease) : IDisposable
        {
            public void Dispose()
            {
                lock (lease.sync)
                {
                    lease.writing = false;
                    if (lease.disposed) Volatile.Write(ref lease.owner.occupied, 0);
                }
            }
        }
    }
}
