//! Offline sync engine: drains the outbox through an injectable transport, then applies
//! incremental pull pages into the local store. Network details stay outside the core crate.

use crate::store::{Change, Store};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize)]
pub struct PushMutation {
    pub mutation_id: String,
    pub entity_type: String,
    pub entity_id: String,
    pub op: String,
    pub base_version: i64,
    pub payload: serde_json::Value,
}

#[derive(Debug, Clone, Serialize)]
pub struct PushRequest {
    pub device_id: String,
    pub mutations: Vec<PushMutation>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct PushResult {
    pub mutation_id: String,
    pub status: String,
    #[serde(default)]
    pub entity_version: Option<i64>,
    #[serde(default)]
    pub retryable: bool,
    #[serde(default)]
    pub server_entity: Option<serde_json::Value>,
}

#[derive(Debug, Clone, Default, Deserialize)]
pub struct PushResponse {
    #[serde(default)]
    pub results: Vec<PushResult>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct PullChange {
    pub revision: i64,
    pub change_index: i64,
    pub entity_type: String,
    pub entity_id: String,
    pub op: String,
    pub version: i64,
    #[serde(default)]
    pub payload: Option<serde_json::Value>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct PullResponse {
    #[serde(default)]
    pub changes: Vec<PullChange>,
    pub next_cursor: String,
    #[serde(default)]
    pub has_more: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TransportError {
    /// Network/5xx/429 style failures worth retrying with backoff.
    Retryable(String),
    /// The pull cursor is older than the server retention window; the shell must reset it.
    CursorExpired,
    /// Authentication or validation failures that need user action.
    Fatal(String),
}

impl std::fmt::Display for TransportError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            TransportError::Retryable(message) => write!(f, "retryable: {message}"),
            TransportError::CursorExpired => write!(f, "cursor expired"),
            TransportError::Fatal(message) => write!(f, "fatal: {message}"),
        }
    }
}

impl std::error::Error for TransportError {}

/// The shell provides the network implementation; tests use a deterministic fake.
pub trait SyncTransport {
    fn push(&mut self, request: &PushRequest) -> Result<PushResponse, TransportError>;
    fn pull(&mut self, cursor: Option<&str>, limit: i64) -> Result<PullResponse, TransportError>;
}

#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize)]
pub struct SyncOutcome {
    pub pushed: usize,
    pub duplicates: usize,
    pub conflicts: Vec<String>,
    pub rejected: usize,
    pub pulled: usize,
    pub skipped_stale: usize,
    pub resolved_conflicts: usize,
    pub cursor: Option<String>,
}

/// One sync pass: push at most 100 pending mutations, then pull pages until exhausted or the
/// page budget is reached. Transport failures return early and never drop outbox rows.
pub fn sync_once(
    store: &mut Store,
    transport: &mut dyn SyncTransport,
    device_id: &str,
    max_pull_pages: usize,
) -> Result<SyncOutcome, TransportError> {
    let mut outcome = SyncOutcome::default();
    let pending = store
        .pending_mutations(100)
        .map_err(|error| TransportError::Fatal(error.to_string()))?;
    if !pending.is_empty() {
        let mutations = pending
            .iter()
            .map(|entry| PushMutation {
                mutation_id: entry.mutation_id.clone(),
                entity_type: entry.entity_type.clone(),
                entity_id: entry.entity_id.clone(),
                op: entry.operation.clone(),
                base_version: entry.base_version,
                payload: serde_json::from_str(&entry.payload).unwrap_or(serde_json::json!({})),
            })
            .collect();
        let response = transport.push(&PushRequest {
            device_id: device_id.to_string(),
            mutations,
        })?;
        let pending_by_id: std::collections::HashMap<_, _> = pending
            .iter()
            .map(|entry| {
                (
                    entry.mutation_id.clone(),
                    (entry.entity_type.clone(), entry.entity_id.clone()),
                )
            })
            .collect();
        for result in response.results {
            match result.status.as_str() {
                "accepted" => {
                    store
                        .remove_mutation(&result.mutation_id)
                        .map_err(|error| TransportError::Fatal(error.to_string()))?;
                    outcome.pushed += 1;
                }
                "duplicate" => {
                    store
                        .remove_mutation(&result.mutation_id)
                        .map_err(|error| TransportError::Fatal(error.to_string()))?;
                    outcome.duplicates += 1;
                }
                "conflict" => {
                    let target = pending_by_id.get(&result.mutation_id);
                    let applied = match (&result.server_entity, target) {
                        (Some(entity), Some((entity_type, entity_id))) => store
                            .force_apply_snapshot(
                                entity_type,
                                entity_id,
                                &entity.to_string(),
                                result.entity_version.unwrap_or(1),
                            )
                            .is_ok(),
                        _ => false,
                    };
                    if applied {
                        store
                            .remove_mutation(&result.mutation_id)
                            .map_err(|error| TransportError::Fatal(error.to_string()))?;
                        outcome.resolved_conflicts += 1;
                    } else {
                        store
                            .record_failure(&result.mutation_id, "VERSION_CONFLICT")
                            .map_err(|error| TransportError::Fatal(error.to_string()))?;
                        outcome.conflicts.push(result.mutation_id.clone());
                    }
                }
                "rejected" => {
                    store
                        .record_failure(&result.mutation_id, "REJECTED")
                        .map_err(|error| TransportError::Fatal(error.to_string()))?;
                    outcome.rejected += 1;
                }
                _ => {
                    store
                        .record_failure(&result.mutation_id, "UNKNOWN_STATUS")
                        .map_err(|error| TransportError::Fatal(error.to_string()))?;
                }
            }
        }
    }

    for _ in 0..max_pull_pages {
        let cursor = store
            .cursor_value()
            .map_err(|error| TransportError::Fatal(error.to_string()))?;
        let page = match transport.pull(cursor.as_deref(), 200) {
            Ok(page) => page,
            Err(TransportError::CursorExpired) => {
                // The server dropped the revision window: restart from the oldest available change.
                store
                    .clear_cursor()
                    .map_err(|error| TransportError::Fatal(error.to_string()))?;
                transport.pull(None, 200)?
            }
            Err(other) => return Err(other),
        };
        let changes: Vec<Change> = page
            .changes
            .iter()
            .filter(|change| matches!(change.entity_type.as_str(), "task" | "habit" | "habit_log"))
            .map(|change| Change {
                entity_type: change.entity_type.clone(),
                entity_id: change.entity_id.clone(),
                operation: change.op.clone(),
                entity_version: change.version,
                payload: change
                    .payload
                    .clone()
                    .unwrap_or(serde_json::json!({}))
                    .to_string(),
            })
            .collect();
        let applied = store
            .apply_pull(&changes)
            .map_err(|error| TransportError::Fatal(error.to_string()))?;
        outcome.pulled += applied.applied;
        outcome.skipped_stale += applied.skipped_stale;
        store
            .set_cursor_value(&page.next_cursor)
            .map_err(|error| TransportError::Fatal(error.to_string()))?;
        outcome.cursor = Some(page.next_cursor.clone());
        if !page.has_more {
            break;
        }
    }
    Ok(outcome)
}

/// Retries only retryable transport failures; backoff is injected so tests never sleep.
pub fn sync_with_retry(
    store: &mut Store,
    transport: &mut dyn SyncTransport,
    device_id: &str,
    max_pull_pages: usize,
    max_attempts: u32,
    mut backoff: impl FnMut(u32),
) -> Result<SyncOutcome, TransportError> {
    let mut attempt = 0;
    loop {
        match sync_once(store, transport, device_id, max_pull_pages) {
            Ok(outcome) => return Ok(outcome),
            Err(TransportError::Retryable(_)) if attempt + 1 < max_attempts => {
                backoff(attempt);
                attempt += 1;
            }
            Err(other) => return Err(other),
        }
    }
}
