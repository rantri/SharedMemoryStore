# C++ Basic Usage

This sample builds with the repository when `SMS_BUILD_SAMPLES=ON`, or against
an installed package through `find_package(SharedMemoryStore CONFIG REQUIRED)`.
It creates a bounded store, publishes binary bytes, acquires a lease, reads the
zero-copy value view, and releases the lease.
