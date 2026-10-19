; Analyzer release tracking

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
SS0001 | Ownership | Warning | Use after disposal
SS0002 | Ownership | Warning | Repeated disposal
SS0003 | Ownership | Warning | Unreleased owned resource
SS0004 | Ownership | Warning | Use after move
SS0005 | Ownership | Warning | Move while borrowed
SS0006 | Ownership | Warning | Conflicting loans
SS0007 | Ownership | Warning | Mutation while borrowed
SS0008 | Ownership | Warning | Borrow escapes lifetime
SS0009 | Ownership | Warning | Disposing borrowed resource
SS0010 | Ownership | Warning | Loan crosses suspension
SS0011 | Ownership | Warning | Overwrite loses ownership
SS0012 | Ownership | Warning | Scoped service lifetime
SS0013 | Ownership | Warning | Read during exclusive borrow
SS0014 | Ownership | Warning | Disposal during borrow
SS0015 | Ownership | Warning | Ownership analysis budget exceeded
SS0016 | Ownership | Warning | NoSuspend contract
SS0020 | SafeSharp.Ownership | Warning | Aggregate ownership requires a disposal contract
SS0021 | SafeSharp.Ownership | Warning | Owned member has no release reachable from disposal
SS0022 | SafeSharp.Ownership | Warning | Ownership-bearing structs remain copyable
SS0023 | SafeSharp.Ownership | Warning | Inconsistent ownership contract
SS0024 | SafeSharp.Ownership | Warning | Ignored MustUse result
SS0025 | SafeSharp.Ownership | Warning | Thread-affine value crosses Task.Run
SS0026 | SafeSharp.Ownership | Warning | Singleton may capture scoped service
SS0027 | SafeSharp.Ownership | Warning | Borrowed parameter stored persistently
SS0028 | SafeSharp.Ownership | Warning | Using resource captured by escaping callback
SS0029 | SafeSharp.Ownership | Info | Service dependency exploration budget exceeded
SS0030 | Ownership | Info | Manual disposal exception safety advisory
SS0031 | Ownership | Warning | Unobserved asynchronous disposal
SS0032 | Ownership | Disabled | Unsafe or dynamic boundary
SS0033 | Ownership | Disabled | Missing ownership return contract
SS0034 | Ownership | Warning | Ownership contract changes across implementation boundaries
