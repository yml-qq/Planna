//! Offline sync engine: drains the outbox through an injectable transport, then applies
//! incremental pull pages into the local store. Network details stay outside the core crate.

use crate::store::{Change, Store};
use serde::{Deserialize, Serialize};

fn error(value: impl std::fmt::Display) -> String {
    value.to_string()
}

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

/// The shell provides the network implementation; tests use a deterministic fake.
pub trait SyncTransport {
    fn push(&mut self, request: &PushRequest) -> Result<PushResponse, String>;
    fn pull(&mut self, cursor: Option<&str>, limit: i64) -> Result<PullResponse, String>;
}

#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize)]
pub struct SyncOutcome {
    pub pushed: usize,
    pub duplicates: usize,
    pub conflicts: Vec<String>,
    pub rejected: usize,
    pub pulled: usize,
    pub skipped_stale: usize,
    pub cursor: Option<String>,
}

/// One sync pass: push at most 100 pending mutations, then pull pages until exhausted or the
/// page budget is reached. Transport failures return early and never drop outbox rows.
pub fn sync_once(
    store: &mut Store,
    transport: &mut dyn SyncTransport,
    device_id: &str,
    max_pull_pages: usize,
) -> Result<SyncOutcome, String> {
    let mut outcome = SyncOutcome::default();
    let pending = store.pending_mutations(100).map_err(error)?;
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
        for result in response.results {
            match result.status.as_str() {
                "accepted" => {
                    store.remove_mutation(&result.mutation_id).map_err(error)?;
                    outcome.pushed += 1;
                }
                "duplicate" => {
                    store.remove_mutation(&result.mutation_id).map_err(error)?;
                    outcome.duplicates += 1;
                }
                "conflict" => {
                    store
                        .record_failure(&result.mutation_id, "VERSION_CONFLICT")
                        .map_err(error)?;
                    outcome.conflicts.push(result.mutation_id.clone());
                }
                "rejected" => {
                    store
                        .record_failure(&result.mutation_id, "REJECTED")
                        .map_err(error)?;
                    outcome.rejected += 1;
                }
                _ => {
                    store
                        .record_failure(&result.mutation_id, "UNKNOWN_STATUS")
                        .map_err(error)?;
                }
            }
        }
    }

    for _ in 0..max_pull_pages {
        let cursor = store.cursor_value().map_err(error)?;
        let page = transport.pull(cursor.as_deref(), 200)?;
        let changes: Vec<Change> = page
            .changes
            .iter()
            .filter(|change| change.entity_type == "task")
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
        let applied = store.apply_pull(&changes).map_err(error)?;
        outcome.pulled += applied.applied;
        outcome.skipped_stale += applied.skipped_stale;
        store.set_cursor_value(&page.next_cursor).map_err(error)?;
        outcome.cursor = Some(page.next_cursor.clone());
        if !page.has_more {
            break;
        }
    }
    Ok(outcome)
}
