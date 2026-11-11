//! Planna desktop local store.
//!
//! Local SQLite is the UI single source of truth: every local mutation and its outbox
//! operation are written in one transaction (AGENTS.md invariant 6). The store never talks
//! to the network; the shell drains `outbox` through the sync API and applies pull changes.

pub mod store;

pub use store::{
    Change, NewTask, OutboxEntry, PullOutcome, Store, StoreError, StoredTask, TaskPatch,
};
