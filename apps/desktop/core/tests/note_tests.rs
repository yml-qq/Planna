use planna_desktop_core::{Change, NewNote, NotePatch, Store, StoreError};

fn note() -> NewNote {
    NewNote {
        id: "note-1".to_string(),
        title: "Plan".to_string(),
        content: "Body".to_string(),
        task_id: Some("task-1".to_string()),
    }
}

#[test]
fn create_note_writes_row_and_outbox_atomically() {
    let mut store = Store::open_in_memory().expect("store");
    assert!(
        store
            .create_note(&note(), "mutation-note-1", "2026-11-22T00:00:00Z")
            .expect("created")
    );
    let notes = store.notes().expect("notes");
    assert_eq!(notes.len(), 1);
    assert_eq!(notes[0].title, "Plan");
    assert_eq!(notes[0].task_id.as_deref(), Some("task-1"));
    assert_eq!(notes[0].version, 1);

    let pending = store.pending_mutations(10).expect("pending");
    assert_eq!(pending.len(), 1);
    assert_eq!(pending[0].entity_type, "note");
    assert_eq!(pending[0].operation, "upsert");
    assert_eq!(pending[0].base_version, 0);
    let payload: serde_json::Value = serde_json::from_str(&pending[0].payload).expect("payload");
    assert_eq!(payload["title"], "Plan");
    assert_eq!(payload["task_id"], "task-1");

    assert!(
        !store
            .create_note(&note(), "mutation-note-1", "2026-11-22T00:00:00Z")
            .expect("replay")
    );
    assert_eq!(store.notes().expect("notes").len(), 1);
    assert_eq!(store.pending_mutations(10).expect("pending").len(), 1);
}

#[test]
fn update_note_uses_version_guard_and_queues_sync_payload() {
    let mut store = Store::open_in_memory().expect("store");
    store
        .create_note(&note(), "mutation-note-1", "2026-11-22T00:00:00Z")
        .expect("created");
    let conflict = store
        .update_note(
            "note-1",
            &NotePatch {
                content: Some("Body 2".to_string()),
                ..NotePatch::default()
            },
            2,
            "mutation-note-2",
            "2026-11-22T00:01:00Z",
        )
        .expect_err("stale version");
    assert!(matches!(
        conflict,
        StoreError::Conflict { server_version: 1 }
    ));

    assert!(
        store
            .update_note(
                "note-1",
                &NotePatch {
                    content: Some("Body 2".to_string()),
                    ..NotePatch::default()
                },
                1,
                "mutation-note-2",
                "2026-11-22T00:01:00Z",
            )
            .expect("updated")
    );
    let notes = store.notes().expect("notes");
    assert_eq!(notes[0].content, "Body 2");
    assert_eq!(notes[0].version, 2);
    let pending = store.pending_mutations(10).expect("pending");
    assert_eq!(pending.len(), 2);
    assert_eq!(pending[1].base_version, 1);
}

#[test]
fn delete_note_queues_tombstone_and_hides_row() {
    let mut store = Store::open_in_memory().expect("store");
    store
        .create_note(&note(), "mutation-note-1", "2026-11-22T00:00:00Z")
        .expect("created");
    assert!(
        store
            .delete_note("note-1", 1, "mutation-note-2", "2026-11-22T00:02:00Z")
            .expect("deleted")
    );
    assert!(store.notes().expect("notes").is_empty());
    let pending = store.pending_mutations(10).expect("pending");
    assert_eq!(pending.len(), 2);
    assert_eq!(pending[1].operation, "delete");
    assert_eq!(pending[1].base_version, 1);
    assert_eq!(pending[1].payload, "{}");
}

#[test]
fn note_validation_rejects_empty_and_oversized_values() {
    let mut store = Store::open_in_memory().expect("store");
    let empty = NewNote {
        id: "note-empty".to_string(),
        title: "   ".to_string(),
        content: "".to_string(),
        task_id: None,
    };
    assert!(matches!(
        store.create_note(&empty, "mutation-empty", "2026-11-22T00:00:00Z"),
        Err(StoreError::Validation(_))
    ));
    let long = NewNote {
        id: "note-long".to_string(),
        title: "a".repeat(201),
        content: "Body".to_string(),
        task_id: None,
    };
    assert!(matches!(
        store.create_note(&long, "mutation-long", "2026-11-22T00:00:00Z"),
        Err(StoreError::Validation(_))
    ));
    assert!(store.notes().expect("notes").is_empty());
    assert!(store.pending_mutations(10).expect("pending").is_empty());
}

#[test]
fn apply_pull_upserts_notes_and_skips_stale_versions() {
    let mut store = Store::open_in_memory().expect("store");
    let change = Change {
        entity_type: "note".to_string(),
        entity_id: "note-remote".to_string(),
        operation: "upsert".to_string(),
        entity_version: 2,
        payload: serde_json::json!({
            "id": "note-remote",
            "title": "Remote",
            "content": "From server",
            "task_id": null,
            "version": 2,
            "created_at": "2026-11-20T00:00:00+00:00",
            "updated_at": "2026-11-22T00:00:00+00:00"
        })
        .to_string(),
    };
    let outcome = store
        .apply_pull(std::slice::from_ref(&change))
        .expect("pulled");
    assert_eq!(outcome.applied, 1);
    assert_eq!(store.notes().expect("notes")[0].title, "Remote");

    let stale = store.apply_pull(&[change]).expect("stale");
    assert_eq!(stale.applied, 0);
    assert_eq!(stale.skipped_stale, 1);
}

#[test]
fn apply_pull_note_tombstone_hides_the_row() {
    let mut store = Store::open_in_memory().expect("store");
    let upsert = Change {
        entity_type: "note".to_string(),
        entity_id: "note-remote".to_string(),
        operation: "upsert".to_string(),
        entity_version: 1,
        payload: serde_json::json!({
            "id": "note-remote",
            "title": "Remote",
            "content": "From server",
            "version": 1,
            "created_at": "2026-11-20T00:00:00+00:00",
            "updated_at": "2026-11-22T00:00:00+00:00"
        })
        .to_string(),
    };
    let delete = Change {
        entity_type: "note".to_string(),
        entity_id: "note-remote".to_string(),
        operation: "delete".to_string(),
        entity_version: 2,
        payload: serde_json::json!({
            "id": "note-remote",
            "version": 2,
            "deleted_at": "2026-11-22T01:00:00+00:00"
        })
        .to_string(),
    };
    store.apply_pull(&[upsert, delete]).expect("pulled");
    assert!(store.notes().expect("notes").is_empty());
}

#[test]
fn force_apply_snapshot_accepts_note_conflicts() {
    let mut store = Store::open_in_memory().expect("store");
    let payload = serde_json::json!({
        "id": "note-1",
        "title": "Server plan",
        "content": "Server body",
        "task_id": null,
        "version": 3,
        "created_at": "2026-11-20T00:00:00+00:00",
        "updated_at": "2026-11-22T00:00:00+00:00"
    })
    .to_string();
    store
        .force_apply_snapshot("note", "note-1", &payload, 3)
        .expect("server snapshot applied");
    let notes = store.notes().expect("notes");
    assert_eq!(notes.len(), 1);
    assert_eq!(notes[0].title, "Server plan");
    assert_eq!(notes[0].version, 3);

    let updated = serde_json::json!({
        "id": "note-1",
        "title": "Server plan v2",
        "content": "Server body",
        "task_id": null,
        "version": 4,
        "created_at": "2026-11-20T00:00:00+00:00",
        "updated_at": "2026-11-22T01:00:00+00:00"
    })
    .to_string();
    store
        .force_apply_snapshot("note", "note-1", &updated, 4)
        .expect("server snapshot replaced");
    assert_eq!(store.notes().expect("notes")[0].version, 4);
}

#[test]
fn notes_matching_searches_title_and_content_case_insensitively() {
    let mut store = Store::open_in_memory().expect("store");
    let first = NewNote {
        id: "note-alpha".to_string(),
        title: "Project Alpha".to_string(),
        content: "Launch checklist".to_string(),
        task_id: None,
    };
    let second = NewNote {
        id: "note-beta".to_string(),
        title: "Meeting".to_string(),
        content: "Discuss ALPHA roadmap".to_string(),
        task_id: None,
    };
    store
        .create_note(&first, "mutation-alpha", "2026-11-22T00:00:00Z")
        .expect("first");
    store
        .create_note(&second, "mutation-beta", "2026-11-22T00:01:00Z")
        .expect("second");
    let ids = store
        .notes_matching("alpha")
        .expect("search")
        .into_iter()
        .map(|note| note.id)
        .collect::<Vec<_>>();
    assert_eq!(ids.len(), 2);
    assert!(ids.contains(&"note-alpha".to_string()));
    assert!(ids.contains(&"note-beta".to_string()));
    assert_eq!(store.notes_matching("checklist").expect("search").len(), 1);
    assert!(store.notes_matching("missing").expect("search").is_empty());
    assert_eq!(store.notes_matching("   ").expect("all").len(), 2);
}
