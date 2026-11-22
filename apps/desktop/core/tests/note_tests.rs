use planna_desktop_core::{NewNote, NotePatch, Store, StoreError};

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
