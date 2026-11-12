use planna_desktop_core::{
    Change, HabitPatchLocal, NewHabit, PullChange, PullResponse, PushResponse, PushResult, Store,
    SyncTransport, sync_once,
};

fn habit() -> NewHabit {
    NewHabit {
        id: "habit-1".to_string(),
        title: "Read".to_string(),
        schedule_type: "daily".to_string(),
        weekdays: None,
        target_quantity: 2,
        unit: "pages".to_string(),
        time_zone: "Asia/Shanghai".to_string(),
    }
}

fn habit_change(version: i64, title: &str, op: &str) -> Change {
    Change {
        entity_type: "habit".to_string(),
        entity_id: "habit-1".to_string(),
        operation: op.to_string(),
        entity_version: version,
        payload: serde_json::json!({
            "id": "habit-1",
            "title": title,
            "schedule_type": "daily",
            "weekdays": null,
            "target_quantity": 2,
            "unit": "pages",
            "time_zone": "Asia/Shanghai",
            "paused_at": null,
            "version": version,
            "updated_at": "2026-11-12T00:00:00Z",
            "deleted_at": null
        })
        .to_string(),
    }
}

fn log_change(version: i64, quantity: i64) -> Change {
    Change {
        entity_type: "habit_log".to_string(),
        entity_id: "log-1".to_string(),
        operation: "upsert".to_string(),
        entity_version: version,
        payload: serde_json::json!({
            "id": "log-1",
            "habit_id": "habit-1",
            "log_date": "2026-11-12",
            "quantity": quantity,
            "status": "done",
            "version": version,
            "updated_at": "2026-11-12T00:00:00Z"
        })
        .to_string(),
    }
}

struct FakeTransport {
    push_results: Vec<PushResult>,
    pull_pages: Vec<PullResponse>,
}

impl SyncTransport for FakeTransport {
    fn push(
        &mut self,
        _request: &planna_desktop_core::PushRequest,
    ) -> Result<PushResponse, planna_desktop_core::TransportError> {
        Ok(PushResponse {
            results: std::mem::take(&mut self.push_results),
        })
    }

    fn pull(
        &mut self,
        cursor: Option<&str>,
        _limit: i64,
    ) -> Result<PullResponse, planna_desktop_core::TransportError> {
        if self.pull_pages.is_empty() {
            return Ok(PullResponse {
                changes: Vec::new(),
                next_cursor: cursor.unwrap_or("done").to_string(),
                has_more: false,
            });
        }
        Ok(self.pull_pages.remove(0))
    }
}

fn store_with_habit() -> Store {
    let mut store = Store::open_in_memory().expect("store");
    store
        .create_habit(&habit(), "mutation-1", "2026-11-12T00:00:00Z")
        .expect("habit");
    store
}

#[test]
fn create_habit_writes_row_and_outbox_atomically() {
    let store = store_with_habit();
    let habits = store.habits().expect("habits");
    assert_eq!(habits.len(), 1);
    assert_eq!(habits[0].title, "Read");
    assert_eq!(habits[0].version, 1);
    let pending = store.pending_mutations(10).expect("pending");
    assert_eq!(pending.len(), 1);
    assert_eq!(pending[0].entity_type, "habit");
    assert_eq!(pending[0].base_version, 0);
    let payload: serde_json::Value = serde_json::from_str(&pending[0].payload).expect("payload");
    assert_eq!(payload["target_quantity"], 2);
    assert_eq!(payload["unit"], "pages");
}

#[test]
fn duplicate_mutation_does_not_duplicate_habit() {
    let mut store = store_with_habit();
    assert!(
        !store
            .create_habit(&habit(), "mutation-1", "2026-11-12T00:00:01Z")
            .expect("replay")
    );
    assert_eq!(store.habits().expect("habits").len(), 1);
    assert_eq!(store.pending_mutations(10).expect("pending").len(), 1);
}

#[test]
fn weekly_habit_requires_weekdays() {
    let mut store = Store::open_in_memory().expect("store");
    let invalid = NewHabit {
        schedule_type: "weekly".to_string(),
        ..habit()
    };
    assert!(
        store
            .create_habit(&invalid, "mutation-1", "2026-11-12T00:00:00Z")
            .is_err()
    );
    assert!(store.habits().expect("habits").is_empty());
    assert!(store.pending_mutations(10).expect("pending").is_empty());
}

#[test]
fn habit_log_upsert_reuses_same_day_row_and_tracks_base_version() {
    let mut store = store_with_habit();
    assert!(
        store
            .upsert_habit_log(
                "habit-1",
                "2026-11-12",
                2,
                "done",
                "mutation-log-1",
                "2026-11-12T01:00:00Z"
            )
            .expect("created")
    );
    let logs = store
        .habit_logs("habit-1", "2026-11-01", "2026-11-30")
        .expect("logs");
    assert_eq!(logs.len(), 1);
    assert_eq!(logs[0].version, 1);
    let first_id = logs[0].id.clone();

    assert!(
        store
            .upsert_habit_log(
                "habit-1",
                "2026-11-12",
                3,
                "done",
                "mutation-log-2",
                "2026-11-12T02:00:00Z"
            )
            .expect("updated")
    );
    let logs = store
        .habit_logs("habit-1", "2026-11-01", "2026-11-30")
        .expect("logs");
    assert_eq!(logs.len(), 1);
    assert_eq!(logs[0].id, first_id);
    assert_eq!(logs[0].version, 2);
    assert_eq!(logs[0].quantity, 3);

    let pending = store.pending_mutations(10).expect("pending");
    assert_eq!(pending.len(), 3);
    let log_entries: Vec<_> = pending
        .iter()
        .filter(|entry| entry.entity_type == "habit_log")
        .collect();
    assert_eq!(log_entries.len(), 2);
    assert_eq!(log_entries[0].base_version, 0);
    assert_eq!(log_entries[1].base_version, 1);
    assert_eq!(log_entries[0].entity_id, log_entries[1].entity_id);
}

#[test]
fn habit_log_rejects_invalid_values_and_missing_habit() {
    let mut store = store_with_habit();
    assert!(
        store
            .upsert_habit_log("habit-1", "2026-11-12", 1, "unknown", "m1", "now")
            .is_err()
    );
    assert!(
        store
            .upsert_habit_log("missing", "2026-11-12", 1, "done", "m2", "now")
            .is_err()
    );
    assert!(
        store
            .upsert_habit_log("habit-1", "2026-11-12", 1001, "done", "m3", "now")
            .is_err()
    );
    assert!(
        store
            .habit_logs("habit-1", "2026-11-01", "2026-11-30")
            .expect("logs")
            .is_empty()
    );
}

#[test]
fn pull_applies_habits_and_logs_with_version_monotonicity() {
    let mut store = Store::open_in_memory().expect("store");
    let outcome = store
        .apply_pull(&[habit_change(1, "From server", "upsert"), log_change(1, 2)])
        .expect("pull");
    assert_eq!(outcome.applied, 2);
    let habits = store.habits().expect("habits");
    assert_eq!(habits[0].title, "From server");
    assert_eq!(habits[0].version, 1);

    let stale = store
        .apply_pull(&[habit_change(1, "Older", "upsert"), log_change(1, 1)])
        .expect("stale");
    assert_eq!(stale.applied, 0);
    assert_eq!(stale.skipped_stale, 2);

    let newer = store
        .apply_pull(&[habit_change(2, "Renamed", "upsert"), log_change(2, 4)])
        .expect("newer");
    assert_eq!(newer.applied, 2);
    assert_eq!(store.habits().expect("habits")[0].title, "Renamed");
    assert_eq!(
        store
            .habit_logs("habit-1", "2026-11-01", "2026-11-30")
            .expect("logs")[0]
            .quantity,
        4
    );

    let deleted = store
        .apply_pull(&[habit_change(3, "Renamed", "delete")])
        .expect("delete");
    assert_eq!(deleted.applied, 1);
    assert!(store.habits().expect("habits").is_empty());
}

#[test]
fn unknown_entities_are_skipped_and_log_delete_ignored() {
    let mut store = Store::open_in_memory().expect("store");
    let unknown = Change {
        entity_type: "tag".to_string(),
        entity_id: "tag-1".to_string(),
        operation: "upsert".to_string(),
        entity_version: 1,
        payload: "{}".to_string(),
    };
    let outcome = store.apply_pull(&[unknown]).expect("pull");
    assert_eq!(outcome.skipped_stale, 1);
    let delete_log = Change {
        operation: "delete".to_string(),
        ..log_change(2, 1)
    };
    let outcome = store.apply_pull(&[delete_log]).expect("pull");
    assert_eq!(outcome.skipped_stale, 1);
}

#[test]
fn sync_engine_pushes_habit_mutations_and_clears_outbox() {
    let mut store = store_with_habit();
    store
        .upsert_habit_log(
            "habit-1",
            "2026-11-12",
            2,
            "done",
            "mutation-log-1",
            "2026-11-12T01:00:00Z",
        )
        .expect("log");
    let mut transport = FakeTransport {
        push_results: vec![
            PushResult {
                mutation_id: "mutation-1".to_string(),
                status: "accepted".to_string(),
                entity_version: Some(1),
                retryable: false,
                server_entity: None,
            },
            PushResult {
                mutation_id: "mutation-log-1".to_string(),
                status: "accepted".to_string(),
                entity_version: Some(1),
                retryable: false,
                server_entity: None,
            },
        ],
        pull_pages: vec![PullResponse {
            changes: vec![PullChange {
                revision: 1,
                change_index: 0,
                entity_type: "habit".to_string(),
                entity_id: "habit-1".to_string(),
                op: "upsert".to_string(),
                version: 2,
                payload: Some(serde_json::json!({
                    "id": "habit-1",
                    "title": "From server",
                    "schedule_type": "daily",
                    "weekdays": null,
                    "target_quantity": 2,
                    "unit": "pages",
                    "time_zone": "Asia/Shanghai",
                    "paused_at": null,
                    "version": 2,
                    "updated_at": "2026-11-12T00:00:00Z",
                    "deleted_at": null
                })),
            }],
            next_cursor: "c1".to_string(),
            has_more: false,
        }],
    };
    let outcome = sync_once(&mut store, &mut transport, "device-1", 2).expect("sync");
    assert_eq!(outcome.pushed, 2);
    assert_eq!(outcome.pulled, 1);
    assert!(store.pending_mutations(10).expect("pending").is_empty());
    assert_eq!(
        store.cursor_value().expect("cursor"),
        Some("c1".to_string())
    );
}

#[test]
fn update_habit_pauses_and_resumes_with_base_version() {
    let mut store = store_with_habit();
    let pause = HabitPatchLocal {
        paused: Some(true),
        ..HabitPatchLocal::default()
    };
    assert!(
        store
            .update_habit("habit-1", &pause, "mutation-2", "2026-11-12T01:00:00Z")
            .expect("paused")
    );
    let habits = store.habits().expect("habits");
    assert_eq!(habits[0].version, 2);
    assert_eq!(habits[0].paused_at.as_deref(), Some("2026-11-12T01:00:00Z"));
    let pending = store.pending_mutations(10).expect("pending");
    let last = pending.last().expect("mutation");
    assert_eq!(last.base_version, 1);
    let payload: serde_json::Value = serde_json::from_str(&last.payload).expect("payload");
    assert_eq!(payload["paused"], true);

    let resume = HabitPatchLocal {
        paused: Some(false),
        ..HabitPatchLocal::default()
    };
    assert!(
        store
            .update_habit("habit-1", &resume, "mutation-3", "2026-11-12T02:00:00Z")
            .expect("resumed")
    );
    let habits = store.habits().expect("habits");
    assert_eq!(habits[0].version, 3);
    assert!(habits[0].paused_at.is_none());
    let pending = store.pending_mutations(10).expect("pending");
    assert_eq!(pending.last().expect("mutation").base_version, 2);
}

#[test]
fn update_habit_edits_title_target_and_unit() {
    let mut store = store_with_habit();
    let patch = HabitPatchLocal {
        title: Some("Read daily".to_string()),
        schedule_type: None,
        weekdays: None,
        target_quantity: Some(30),
        unit: Some("minutes".to_string()),
        paused: None,
    };
    assert!(
        store
            .update_habit("habit-1", &patch, "mutation-2", "2026-11-12T01:00:00Z")
            .expect("updated")
    );
    let habits = store.habits().expect("habits");
    assert_eq!(habits[0].title, "Read daily");
    assert_eq!(habits[0].target_quantity, 30);
    assert_eq!(habits[0].unit, "minutes");
    assert_eq!(habits[0].version, 2);
}

#[test]
fn update_habit_rejects_invalid_patch_and_replays_idempotently() {
    let mut store = store_with_habit();
    assert!(
        store
            .update_habit("habit-1", &HabitPatchLocal::default(), "mutation-2", "now")
            .is_err()
    );
    let invalid = HabitPatchLocal {
        target_quantity: Some(0),
        ..HabitPatchLocal::default()
    };
    assert!(
        store
            .update_habit("habit-1", &invalid, "mutation-2", "now")
            .is_err()
    );
    let pause = HabitPatchLocal {
        paused: Some(true),
        ..HabitPatchLocal::default()
    };
    assert!(
        store
            .update_habit("habit-1", &pause, "mutation-2", "2026-11-12T01:00:00Z")
            .expect("paused")
    );
    assert!(
        !store
            .update_habit("habit-1", &pause, "mutation-2", "2026-11-12T02:00:00Z")
            .expect("replay")
    );
    assert_eq!(store.habits().expect("habits")[0].version, 2);
}

#[test]
fn update_habit_changes_schedule_to_weekly_and_clears_weekdays_when_daily() {
    let mut store = store_with_habit();
    let weekly = HabitPatchLocal {
        schedule_type: Some("weekly".to_string()),
        weekdays: Some(vec![5, 1, 3]),
        ..HabitPatchLocal::default()
    };
    assert!(
        store
            .update_habit("habit-1", &weekly, "mutation-2", "2026-11-12T01:00:00Z")
            .expect("weekly")
    );
    let habits = store.habits().expect("habits");
    assert_eq!(habits[0].schedule_type, "weekly");
    assert_eq!(habits[0].weekdays.as_deref(), Some([1, 3, 5].as_slice()));
    let daily = HabitPatchLocal {
        schedule_type: Some("daily".to_string()),
        ..HabitPatchLocal::default()
    };
    assert!(
        store
            .update_habit("habit-1", &daily, "mutation-3", "2026-11-12T02:00:00Z")
            .expect("daily")
    );
    assert!(store.habits().expect("habits")[0].weekdays.is_none());
}

#[test]
fn conflict_resolution_retry_and_overwrite_reset_outbox_state() {
    let mut store = store_with_habit();
    store
        .record_failure("mutation-1", "VERSION_CONFLICT")
        .expect("failure");
    store
        .record_conflict(
            "mutation-1",
            "habit",
            "habit-1",
            "{\"id\":\"habit-1\"}",
            5,
            "2026-11-12T00:00:00Z",
        )
        .expect("conflict");
    assert!(store.resolve_conflict_retry("mutation-1").expect("retry"));
    let entry = &store.pending_mutations(10).expect("pending")[0];
    assert_eq!(entry.attempts, 0);
    assert!(entry.last_error.is_none());
    assert!(store.conflicts().expect("conflicts").is_empty());

    store
        .record_conflict(
            "mutation-1",
            "habit",
            "habit-1",
            "{\"id\":\"habit-1\"}",
            5,
            "2026-11-12T00:00:00Z",
        )
        .expect("conflict");
    assert!(
        store
            .resolve_conflict_overwrite("mutation-1")
            .expect("overwrite")
    );
    let entry = &store.pending_mutations(10).expect("pending")[0];
    assert_eq!(entry.base_version, 5);
    assert!(store.conflicts().expect("conflicts").is_empty());
    assert!(!store.resolve_conflict_retry("missing").expect("missing"));
}

#[test]
fn prune_conflicts_drops_entries_without_pending_mutation() {
    let mut store = store_with_habit();
    store
        .record_conflict("mutation-1", "habit", "habit-1", "{}", 2, "now")
        .expect("existing");
    store
        .record_conflict("orphan", "habit", "habit-1", "{}", 2, "now")
        .expect("orphan");
    assert_eq!(store.prune_conflicts().expect("prune"), 1);
    let conflicts = store.conflicts().expect("conflicts");
    assert_eq!(conflicts.len(), 1);
    assert_eq!(conflicts[0].mutation_id, "mutation-1");
}
