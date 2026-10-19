# Advanced ownership protocols

SafeSharp models these protocols in its language-independent intermediate representation and fixed-point state. The C# frontend decides which source operations have those effects; annotations alone do not change C# runtime semantics.

## Two-phase exclusive borrowing

`ReserveExclusiveBorrow(source: owner, target: reservation)` creates a live exclusive reservation. `ActivateBorrow(source: reservation)` activates the same loan, including when the source is an alias of the original holder. `BorrowExclusive` remains immediately active, so an explicit mutable borrow does not silently gain more permissive semantics.

| Operation on overlapping storage | Reserved phase | Active phase |
| --- | --- | --- |
| Read through the owner | Allowed | `SS0013` |
| Create a shared loan | Allowed | `SS0006` |
| Create another exclusive loan or reservation | `SS0006` | `SS0006` |
| Mutate through the owner | `SS0007` | `SS0007` |
| Mutate through this loan | `SS0007` | Allowed |
| Move or transfer ownership | `SS0005` | `SS0005` |
| Dispose the owner | `SS0014` | `SS0014` |

Activation checks for other live overlapping loans and emits `SS0006` if a shared argument or competing loan still exists. A shared argument whose last use precedes activation has already expired through backward place liveness. A reservation that is never activated can likewise end at its last use; it does not have to survive to the end of the lexical scope. Disjoint field projections retain their existing independent-borrow behavior. Reservation and active phases both obey the loan's suspension policy.

The phase is a flags lattice: a branch join may retain `Reserved | Active`. Reads and new shared borrows are then diagnosed as possible conflicts. Activation on every incoming branch produces an active loan. Loan identity, phase, parent ancestry, absence, and activation evidence participate in joins and fixed-point equality; activation is not stored in an external instruction-side cache. Reports expose `LoanSnapshot.Phase` and immutable `ActivationSites`, and JSON/HTML include those values.

### Relationship to Rust

The design follows Rust's distinction between reserving an implicit mutable borrow and activating it at use. Rust applies this to selected compiler-created borrows, including mutable method receivers; explicit mutable references remain immediate. See the [Rust Compiler Development Guide on two-phase borrows](https://rustc-dev-guide.rust-lang.org/borrow-check/two-phase-borrows.html) and the pinned [Rust 1.91.0 borrow-set implementation](https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/borrow_set.rs).

SafeSharp computes possible phases through its abstract CFG state. It does not import Rust MIR, use Rust's exact dominance query for activation, or claim that arbitrary C# method calls satisfy Rust's lifetime guarantees. The frontend must emit reservation and activation only for operations whose contracts justify those effects.
