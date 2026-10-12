# Cursor allocation measurement qualification

PR #7 passed its full pre-merge Application run (38228258165). The identical production tree then passed standalone Validate (38230236190), but the main Application run (38230236145) measured 24,624 managed bytes in the cursor allocation fixture and correctly blocked deployment. All other 590 core cases passed. This evidence does not identify the individual allocating component or establish a behavioral cursor defect.

The previous test put its warmup and measured loop inside the same tiered test lambda. The revised fixture follows the existing mesh-metadata measurement pattern: a `NoInlining | AggressiveOptimization` test helper is compiled before entering its allocation interval. That attribute bypasses the first tier for the helper, not for production chart methods. No library or application JIT settings change.

The helper first records a fixed 10,000-call warmup. Three subsequent 10,000-call windows must EACH report exactly zero managed bytes. This is not a best-of-N benchmark or a retry-until-pass loop. Any failing measured window fails the test. The zero-notification assertion remains, and an intentional escaped per-call allocation fixture verifies that the probe can detect allocations. All windows and warmup are written to JSON before assertions; elapsed ticks, serialization, console output and report allocation are outside the measured operation interval.

The qualification workflow executes the complete suite in three independent processes with normal runtime settings and three with tiered compilation disabled. Every process must succeed; results from failed processes remain artifacts. The usual Application workflow still tests the normal production configuration, both Uno targets, all browser behavior, native GPU rendering and package consumers. No failure gate is bypassed.

This is a warmed synchronization bookkeeping claim, not zero-allocation startup, zero-allocation pointer hit testing, a GPU result, FPS or end-to-end latency. The exact source commit, workflow conclusion and recorded windows are necessary evidence; a successful older run cannot certify a changed source tree.

References:
- https://learn.microsoft.com/dotnet/api/system.runtime.compilerservices.methodimploptions
- https://learn.microsoft.com/dotnet/api/system.gc.getallocatedbytesforcurrentthread
