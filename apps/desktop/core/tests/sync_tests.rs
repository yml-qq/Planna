use planna_desktop_core::{
    NewTask, PullChange, PullResponse, PushResponse, PushResult, Store, SyncTransport,
    TransportError, sync_once, sync_with_retry,
};

fn task_payload(id: &str, title: &str, version: i64) -> serde_json::Value {
    serde_json::json!({
        "id": id,
        "list_id": "list-inbox",
        "title": title,
        "description": "",
        "status": "todo",
        "priority": 0,
        "all_day": false,
        "time_zone": "Asia/Shanghai",
        "due_at": null,
        "due_local_date": null,
        "version": version,
        "updated_at": "2026-11-12T00:00:00Z",
        "deleted_at": null
    })
}

fn change(id: &str, title: &str, version: i64, op: &str) -> PullChange {
    PullChange {
        revision: version,
        change_index: 0,
        entity_type: "task".to_string(),
        entity_id: id.to_string(),
        op: op.to_string(),
        version,
        payload: Some(task_payload(id, title, version)),
    }
}

fn page(changes: Vec<PullChange>, cursor: &str, has_more: bool) -> PullResponse {
    PullResponse {
        changes,
        next_cursor: cursor.to_string(),
        has_more,
    }
}

struct FakeTransport {
    push_results: Vec<PushResult>,
    pull_pages: Vec<PullResponse>,
    push_calls: usize,
    pull_cursors: Vec<Option<String>>,
    fail_push: bool,
    fail_pull: bool,
    retryable_push_failures: usize,
    cursor_expired_pulls: usize,
}

impl FakeTransport {
    fn new(push_results: Vec<PushResult>, pull_pages: Vec<PullResponse>) -> Self {
        Self {
            push_results,
            pull_pages,
            push_calls: 0,
            pull_cursors: Vec::new(),
            fail_push: false,
            fail_pull: false,
            retryable_push_failures: 0,
            cursor_expired_pulls: 0,
        }
    }
}

impl SyncTransport for FakeTransport {
    fn push(
        &mut self,
        _request: &planna_desktop_core::PushRequest,
    ) -> Result<PushResponse, TransportError> {
        self.push_calls += 1;
        if self.fail_push {
            return Err(TransportError::Fatal("offline".to_string()));
        }
        if self.retryable_push_failures > 0 {
            self.retryable_push_failures -= 1;
            return Err(TransportError::Retryable("network".to_string()));
        }
        Ok(PushResponse {
            results: self.push_results.clone(),
        })
    }

    fn pull(&mut self, cursor: Option<&str>, _limit: i64) -> Result<PullResponse, TransportError> {
        self.pull_cursors.push(cursor.map(str::to_string));
        if self.fail_pull {
            return Err(TransportError::Fatal("offline".to_string()));
        }
        if self.cursor_expired_pulls > 0 {
            self.cursor_expired_pulls -= 1;
            return Err(TransportError::CursorExpired);
        }
        if self.pull_pages.is_empty() {
            return Ok(page(Vec::new(), cursor.unwrap_or("empty"), false));
        }
        Ok(self.pull_pages.remove(0))
    }
}

fn accepted(mutation_id: &str) -> PushResult {
    PushResult {
        mutation_id: mutation_id.to_string(),
        status: "accepted".to_string(),
        entity_version: Some(1),
        retryable: false,
        server_entity: None,
    }
}

fn store_with_task() -> Store {
    let mut store = Store::open_in_memory().expect("store");
    store
        .upsert_list("list-inbox", "Inbox", "inbox", 1)
        .expect("list");
    store
        .create_task(
            &NewTask {
                id: "task-1".to_string(),
                list_id: "list-inbox".to_string(),
                title: "Local".to_string(),
                time_zone: "Asia/Shanghai".to_string(),
                all_day: false,
                due_at: None,
                due_local_date: None,
            },
            "mutation-1",
            "2026-11-12T00:00:00Z",
        )
        .expect("created");
    store
}

#[test]
fn push_success_clears_outbox_then_pulls() {
    let mut store = store_with_task();
    let mut transport = FakeTransport::new(
        vec![accepted("mutation-1")],
        vec![page(
            vec![change("task-remote", "Remote", 2, "upsert")],
            "c1",
            false,
        )],
    );
    let outcome = sync_once(&mut store, &mut transport, "device-1", 5).expect("sync");
    assert_eq!(outcome.pushed, 1);
    assert_eq!(outcome.pulled, 1);
    assert_eq!(outcome.cursor.as_deref(), Some("c1"));
    assert!(store.pending_mutations(10).expect("pending").is_empty());
    assert_eq!(
        store.cursor_value().expect("cursor"),
        Some("c1".to_string())
    );
    assert_eq!(store.tasks_for_list("list-inbox").expect("tasks").len(), 2);
    assert_eq!(transport.pull_cursors, vec![None]);
}

#[test]
fn duplicate_results_are_dropped_without_retry() {
    let mut store = store_with_task();
    let mut transport = FakeTransport::new(
        vec![PushResult {
            mutation_id: "mutation-1".to_string(),
            status: "duplicate".to_string(),
            entity_version: Some(1),
            retryable: false,
            server_entity: None,
        }],
        vec![],
    );
    let outcome = sync_once(&mut store, &mut transport, "device-1", 1).expect("sync");
    assert_eq!(outcome.duplicates, 1);
    assert!(store.pending_mutations(10).expect("pending").is_empty());
}

#[test]
fn conflicts_keep_outbox_and_record_attempts() {
    let mut store = store_with_task();
    let mut transport = FakeTransport::new(
        vec![PushResult {
            mutation_id: "mutation-1".to_string(),
            status: "conflict".to_string(),
            entity_version: Some(2),
            retryable: false,
            server_entity: None,
        }],
        vec![],
    );
    let outcome = sync_once(&mut store, &mut transport, "device-1", 1).expect("sync");
    assert_eq!(outcome.conflicts, vec!["mutation-1".to_string()]);
    let pending = store.pending_mutations(10).expect("pending");
    assert_eq!(pending.len(), 1);
    assert_eq!(pending[0].attempts, 1);
}

#[test]
fn transport_failure_keeps_outbox_and_skips_pull() {
    let mut store = store_with_task();
    let mut transport = FakeTransport::new(vec![], vec![]);
    transport.fail_push = true;
    assert_eq!(
        sync_once(&mut store, &mut transport, "device-1", 3),
        Err(TransportError::Fatal("offline".to_string()))
    );
    assert_eq!(store.pending_mutations(10).expect("pending").len(), 1);
    assert!(transport.pull_cursors.is_empty());
}

#[test]
fn multi_page_pull_advances_cursor_and_skips_stale_versions() {
    let mut store = store_with_task();
    let mut transport = FakeTransport::new(
        vec![accepted("mutation-1")],
        vec![
            page(vec![change("task-remote", "New", 3, "upsert")], "c1", true),
            page(vec![change("task-remote", "Old", 2, "upsert")], "c2", false),
        ],
    );
    let outcome = sync_once(&mut store, &mut transport, "device-1", 5).expect("sync");
    assert_eq!(outcome.pulled, 1);
    assert_eq!(outcome.skipped_stale, 1);
    assert_eq!(outcome.cursor.as_deref(), Some("c2"));
    assert_eq!(transport.pull_cursors, vec![None, Some("c1".to_string())]);
    assert_eq!(
        store.tasks_for_list("list-inbox").expect("tasks")[1].version,
        3
    );
}

#[test]
fn pull_applies_tombstones_and_ignores_non_task_entities() {
    let mut store = store_with_task();
    let mut transport = FakeTransport::new(
        vec![accepted("mutation-1")],
        vec![page(
            vec![
                PullChange {
                    revision: 2,
                    change_index: 0,
                    entity_type: "tag".to_string(),
                    entity_id: "tag-1".to_string(),
                    op: "upsert".to_string(),
                    version: 1,
                    payload: None,
                },
                change("task-1", "Local", 2, "delete"),
            ],
            "c1",
            false,
        )],
    );
    let outcome = sync_once(&mut store, &mut transport, "device-1", 2).expect("sync");
    assert_eq!(outcome.pulled, 1);
    assert!(
        store
            .tasks_for_list("list-inbox")
            .expect("tasks")
            .is_empty()
    );
    assert_eq!(
        store.cursor_value().expect("cursor"),
        Some("c1".to_string())
    );
}

#[test]
fn empty_outbox_still_pulls_and_respects_page_budget() {
    let mut store = Store::open_in_memory().expect("store");
    store
        .upsert_list("list-inbox", "Inbox", "inbox", 1)
        .expect("list");
    let mut transport = FakeTransport::new(
        vec![],
        vec![
            page(vec![change("task-a", "A", 1, "upsert")], "c1", true),
            page(vec![change("task-b", "B", 1, "upsert")], "c2", false),
        ],
    );
    let outcome = sync_once(&mut store, &mut transport, "device-1", 1).expect("sync");
    assert_eq!(transport.push_calls, 0);
    assert_eq!(outcome.pulled, 1);
    assert_eq!(outcome.cursor.as_deref(), Some("c1"));
    assert_eq!(
        store.cursor_value().expect("cursor"),
        Some("c1".to_string())
    );
}

#[test]
fn retryable_push_failure_retries_with_backoff() {
    let mut store = store_with_task();
    let mut transport = FakeTransport::new(vec![accepted("mutation-1")], vec![]);
    transport.retryable_push_failures = 2;
    let mut attempts = Vec::new();
    let outcome = sync_with_retry(&mut store, &mut transport, "device-1", 1, 3, |attempt| {
        attempts.push(attempt);
    })
    .expect("sync");
    assert_eq!(outcome.pushed, 1);
    assert_eq!(transport.push_calls, 3);
    assert_eq!(attempts, vec![0, 1]);
}

#[test]
fn fatal_push_failure_is_not_retried() {
    let mut store = store_with_task();
    let mut transport = FakeTransport::new(vec![accepted("mutation-1")], vec![]);
    transport.fail_push = true;
    let mut attempts = Vec::new();
    let result = sync_with_retry(&mut store, &mut transport, "device-1", 1, 5, |attempt| {
        attempts.push(attempt);
    });
    assert!(matches!(result, Err(TransportError::Fatal(_))));
    assert_eq!(transport.push_calls, 1);
    assert!(attempts.is_empty());
    assert_eq!(store.pending_mutations(10).expect("pending").len(), 1);
}

#[test]
fn cursor_expired_clears_cursor_and_repulls() {
    let mut store = store_with_task();
    store.set_cursor_value("old-cursor").expect("cursor");
    let mut transport = FakeTransport::new(
        vec![accepted("mutation-1")],
        vec![page(
            vec![change("task-a", "A", 1, "upsert")],
            "fresh",
            false,
        )],
    );
    transport.cursor_expired_pulls = 1;
    let outcome = sync_once(&mut store, &mut transport, "device-1", 2).expect("sync");
    assert_eq!(outcome.pulled, 1);
    assert_eq!(outcome.cursor.as_deref(), Some("fresh"));
    assert_eq!(
        transport.pull_cursors,
        vec![Some("old-cursor".to_string()), None]
    );
    assert_eq!(
        store.cursor_value().expect("cursor"),
        Some("fresh".to_string())
    );
}

#[test]
fn repeated_cursor_expiry_is_reported() {
    let mut store = Store::open_in_memory().expect("store");
    let mut transport = FakeTransport::new(vec![], vec![]);
    transport.cursor_expired_pulls = 2;
    assert_eq!(
        sync_once(&mut store, &mut transport, "device-1", 1),
        Err(TransportError::CursorExpired)
    );
}

#[test]
fn conflict_with_server_entity_applies_server_state_and_clears_outbox() {
    let mut store = store_with_task();
    let mut transport = FakeTransport::new(
        vec![PushResult {
            mutation_id: "mutation-1".to_string(),
            status: "conflict".to_string(),
            entity_version: Some(2),
            retryable: false,
            server_entity: Some(serde_json::json!({
                "id": "task-1",
                "list_id": "list-inbox",
                "title": "From server",
                "description": "",
                "status": "todo",
                "priority": 0,
                "all_day": false,
                "time_zone": "Asia/Shanghai",
                "due_at": null,
                "due_local_date": null,
                "version": 2,
                "updated_at": "2026-11-12T00:00:00Z",
                "deleted_at": null
            })),
        }],
        vec![],
    );
    let outcome = sync_once(&mut store, &mut transport, "device-1", 1).expect("sync");
    assert_eq!(outcome.resolved_conflicts, 1);
    assert!(outcome.conflicts.is_empty());
    assert!(store.pending_mutations(10).expect("pending").is_empty());
    let tasks = store.tasks_for_list("list-inbox").expect("tasks");
    assert_eq!(tasks[0].title, "From server");
    assert_eq!(tasks[0].version, 2);
}
