use planna_desktop_core::{Change, NewTask, Store, StoreError, TaskPatch};

fn list(store: &Store) {
    store
        .upsert_list("list-inbox", "Inbox", "inbox", 1)
        .expect("list inserted");
}

fn task() -> NewTask {
    NewTask {
        id: "task-1".to_string(),
        list_id: "list-inbox".to_string(),
        title: "Write the offline slice".to_string(),
        time_zone: "Asia/Shanghai".to_string(),
        all_day: false,
        due_at: Some("2026-12-01T10:00:00Z".to_string()),
        due_local_date: None,
    }
}

fn task_count(store: &Store) -> i64 {
    store
        .pending_mutations(100)
        .map(|entries| entries.len() as i64)
        .unwrap_or(-1)
}

#[test]
fn create_task_writes_task_and_outbox_atomically() {
    let mut store = Store::open_in_memory().expect("store");
    list(&store);
    assert!(
        store
            .create_task(&task(), "mutation-1", "2026-11-12T00:00:00Z")
            .expect("created")
    );
    let tasks = store.tasks_for_list("list-inbox").expect("tasks");
    assert_eq!(tasks.len(), 1);
    assert_eq!(tasks[0].title, "Write the offline slice");
    assert_eq!(tasks[0].version, 1);
    assert!(tasks[0].deleted_at.is_none());
    let pending = store.pending_mutations(10).expect("pending");
    assert_eq!(pending.len(), 1);
    assert_eq!(pending[0].operation, "upsert");
    assert_eq!(pending[0].entity_id, "task-1");
    let payload: serde_json::Value =
        serde_json::from_str(&pending[0].payload).expect("payload json");
    assert_eq!(payload["title"], "Write the offline slice");
}

#[test]
fn duplicate_mutation_is_idempotent() {
    let mut store = Store::open_in_memory().expect("store");
    list(&store);
    assert!(
        store
            .create_task(&task(), "mutation-1", "2026-11-12T00:00:00Z")
            .expect("created")
    );
    assert!(
        !store
            .create_task(&task(), "mutation-1", "2026-11-12T00:00:01Z")
            .expect("replay")
    );
    assert_eq!(store.tasks_for_list("list-inbox").expect("tasks").len(), 1);
    assert_eq!(task_count(&store), 1);
}

#[test]
fn failed_validation_rolls_back_task_and_outbox() {
    let mut store = Store::open_in_memory().expect("store");
    list(&store);
    let invalid = NewTask {
        title: "   ".to_string(),
        ..task()
    };
    assert!(matches!(
        store.create_task(&invalid, "mutation-1", "2026-11-12T00:00:00Z"),
        Err(StoreError::Validation(_))
    ));
    assert!(
        store
            .tasks_for_list("list-inbox")
            .expect("tasks")
            .is_empty()
    );
    assert!(store.pending_mutations(10).expect("pending").is_empty());
}

#[test]
fn unknown_list_fails_foreign_key_and_rolls_back() {
    let mut store = Store::open_in_memory().expect("store");
    let orphan = NewTask {
        list_id: "missing".to_string(),
        ..task()
    };
    assert!(matches!(
        store.create_task(&orphan, "mutation-1", "2026-11-12T00:00:00Z"),
        Err(StoreError::Sqlite(_))
    ));
    assert!(store.pending_mutations(10).expect("pending").is_empty());
}

#[test]
fn update_checks_version_and_writes_outbox() {
    let mut store = Store::open_in_memory().expect("store");
    list(&store);
    store
        .create_task(&task(), "mutation-1", "2026-11-12T00:00:00Z")
        .expect("created");
    let patch = TaskPatch {
        title: Some("Renamed".to_string()),
        status: Some("in_progress".to_string()),
        priority: Some(2),
    };
    assert!(
        store
            .update_task("task-1", &patch, 1, "mutation-2", "2026-11-12T00:01:00Z")
            .expect("updated")
    );
    let tasks = store.tasks_for_list("list-inbox").expect("tasks");
    assert_eq!(tasks[0].title, "Renamed");
    assert_eq!(tasks[0].status, "in_progress");
    assert_eq!(tasks[0].version, 2);
    let pending = store.pending_mutations(10).expect("pending");
    assert_eq!(pending.len(), 2);
    let payload: serde_json::Value =
        serde_json::from_str(&pending[1].payload).expect("payload json");
    assert_eq!(payload["version"], 2);
    assert!(matches!(
        store.update_task("task-1", &patch, 1, "mutation-3", "2026-11-12T00:02:00Z"),
        Err(StoreError::Conflict { server_version: 2 })
    ));
    assert!(
        !store
            .update_task("task-1", &patch, 2, "mutation-2", "2026-11-12T00:03:00Z")
            .expect("replay")
    );
    assert_eq!(store.pending_mutations(10).expect("pending").len(), 2);
}

#[test]
fn outbox_failure_tracking_and_removal() {
    let mut store = Store::open_in_memory().expect("store");
    list(&store);
    store
        .create_task(&task(), "mutation-1", "2026-11-12T00:00:00Z")
        .expect("created");
    store
        .record_failure("mutation-1", "VERSION_CONFLICT")
        .expect("failure");
    let entries = store.pending_mutations(10).expect("pending");
    assert_eq!(entries[0].attempts, 1);
    assert_eq!(entries[0].last_error.as_deref(), Some("VERSION_CONFLICT"));
    store.remove_mutation("mutation-1").expect("removed");
    assert!(store.pending_mutations(10).expect("pending").is_empty());
}

#[test]
fn pull_applies_newer_versions_and_skips_stale_ones() {
    let mut store = Store::open_in_memory().expect("store");
    let change = |version: i64, title: &str| Change {
        entity_type: "task".to_string(),
        entity_id: "task-1".to_string(),
        operation: "upsert".to_string(),
        entity_version: version,
        payload: serde_json::json!({
            "id": "task-1",
            "list_id": "list-remote",
            "title": title,
            "status": "todo",
            "priority": 1,
            "all_day": false,
            "time_zone": "Asia/Shanghai",
            "due_at": null,
            "due_local_date": null,
            "version": version,
            "updated_at": "2026-11-12T00:00:00Z",
            "deleted_at": null
        })
        .to_string(),
    };
    let outcome = store.apply_pull(&[change(2, "From server")]).expect("pull");
    assert_eq!(outcome.applied, 1);
    assert_eq!(outcome.skipped_stale, 0);
    let tasks = store.tasks_for_list("list-remote").expect("tasks");
    assert_eq!(tasks[0].version, 2);

    let stale = store.apply_pull(&[change(1, "Older")]).expect("pull stale");
    assert_eq!(stale.applied, 0);
    assert_eq!(stale.skipped_stale, 1);
    assert_eq!(
        store.tasks_for_list("list-remote").expect("tasks")[0].title,
        "From server"
    );

    let deleted = Change {
        entity_type: "task".to_string(),
        entity_id: "task-1".to_string(),
        operation: "delete".to_string(),
        entity_version: 3,
        payload: serde_json::json!({
            "id": "task-1",
            "list_id": "list-remote",
            "title": "From server",
            "version": 3,
            "updated_at": "2026-11-12T00:00:02Z"
        })
        .to_string(),
    };
    let outcome = store.apply_pull(&[deleted]).expect("pull delete");
    assert_eq!(outcome.applied, 1);
    assert!(
        store
            .tasks_for_list("list-remote")
            .expect("tasks")
            .is_empty()
    );
}

#[test]
fn cursor_round_trips() {
    let store = Store::open_in_memory().expect("store");
    assert_eq!(store.cursor_value().expect("empty"), None);
    store.set_cursor_value("cursor-7").expect("cursor");
    assert_eq!(
        store.cursor_value().expect("cursor"),
        Some("cursor-7".to_string())
    );
}

#[test]
fn file_store_persists_across_reopen() {
    let directory = tempfile::tempdir().expect("tempdir");
    let path = directory.path().join("planna.db");
    {
        let mut store = Store::open(&path).expect("store");
        list(&store);
        store
            .create_task(&task(), "mutation-1", "2026-11-12T00:00:00Z")
            .expect("created");
        store.set_cursor_value("cursor-3").expect("cursor");
    }
    let store = Store::open(&path).expect("reopen");
    assert_eq!(store.tasks_for_list("list-inbox").expect("tasks").len(), 1);
    assert_eq!(
        store.cursor_value().expect("cursor"),
        Some("cursor-3".to_string())
    );
}
