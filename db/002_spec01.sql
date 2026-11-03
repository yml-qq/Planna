-- SPEC-01 PostgreSQL design draft. DATA-01 must translate/review as EF Core migration.
-- Do not run in production as a standalone migration.

-- New AI plan previews must store only the hash of their short-lived confirmation token.
-- Existing drafts should expire before enforcing a non-null application invariant.
ALTER TABLE ai_plans ADD COLUMN confirmation_token_hash text;

-- Persist the user's accepted suggested duration on the resulting step.
ALTER TABLE task_steps ADD COLUMN estimate_minutes smallint CHECK(estimate_minutes BETWEEN 1 AND 1440);

CREATE TABLE ai_breakdown_drafts (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 owner_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
 task_id uuid NOT NULL,
 task_version bigint NOT NULL CHECK(task_version >= 1),
 status text NOT NULL DEFAULT 'awaiting_confirmation' CHECK(status IN ('awaiting_confirmation','applied','expired','rejected')),
 snapshot_hash text NOT NULL,
 steps jsonb NOT NULL CHECK(jsonb_typeof(steps) = 'array' AND jsonb_array_length(steps) BETWEEN 1 AND 20),
 assumptions jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(assumptions) = 'array'),
 confirmation_token_hash text NOT NULL,
 expires_at timestamptz NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 applied_at timestamptz,
 FOREIGN KEY (owner_id, task_id) REFERENCES tasks(owner_id, id),
 UNIQUE(owner_id, id)
);
CREATE INDEX idx_ai_breakdowns_owner_task ON ai_breakdown_drafts(owner_id, task_id, created_at DESC);

CREATE TABLE account_exports (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 owner_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
 status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','ready','failed','expired')),
 snapshot_at timestamptz,
 storage_key text,
 error_code text,
 requested_at timestamptz NOT NULL DEFAULT now(),
 ready_at timestamptz,
 expires_at timestamptz,
 CHECK ((status = 'ready' AND storage_key IS NOT NULL AND ready_at IS NOT NULL AND expires_at IS NOT NULL) OR status <> 'ready'),
 UNIQUE(owner_id, id)
);
CREATE INDEX idx_account_exports_owner_time ON account_exports(owner_id, requested_at DESC);

CREATE TABLE account_deletion_requests (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 owner_id uuid NOT NULL UNIQUE REFERENCES users(id) ON DELETE CASCADE,
 status text NOT NULL DEFAULT 'accepted' CHECK(status IN ('accepted','purging','completed','failed')),
 policy_version text NOT NULL,
 requested_at timestamptz NOT NULL DEFAULT now(),
 completed_at timestamptz,
 last_error_code text
);

-- The application must atomically set users.deleted_at, revoke all device_sessions,
-- insert account_deletion_requests, and persist the idempotency result.
-- No client mutation may be accepted when users.deleted_at IS NOT NULL.
-- Store only hashes of short-lived confirmation tokens, never their plaintext.
