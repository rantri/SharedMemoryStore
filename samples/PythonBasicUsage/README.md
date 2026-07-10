# Python basic usage sample

Build and install the platform wheel into a clean environment, then run:

```powershell
python main.py
```

The sample creates an isolated named store, publishes binary payload and
descriptor bytes, reads them through an ownership-safe zero-copy lease, removes
the value, and takes a diagnostics snapshot. Run it against an installed wheel;
the repository does not place the Python source root on `sys.path` for it.
