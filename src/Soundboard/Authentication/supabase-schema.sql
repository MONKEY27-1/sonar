-- Soundboard account system — Supabase schema setup
--
-- Run this ONCE in your Supabase project's SQL Editor (Dashboard → SQL Editor → New query)
-- before the app's login/register screens will work at all. Nothing in the C# code creates
-- this schema for you — Supabase gives you auth.users out of the box, but application-level
-- profile data (username, license, etc.) needs its own table, which is the standard,
-- documented Supabase pattern.

-- 1. The profiles table itself.
-- One row per user, keyed by the same id Supabase Auth uses in auth.users.
create table if not exists public.profiles (
    id uuid primary key references auth.users(id) on delete cascade,
    username text unique not null,
    display_name text,
    email text not null,
    created_at timestamptz not null default now(),
    is_beta_tester boolean not null default false,
    license text not null default 'Free',
    cloud_enabled boolean not null default false,
    country text,
    language text
);

-- 2. Auto-create a profile row the moment someone signs up.
--
-- This runs server-side with elevated privileges (SECURITY DEFINER), which matters
-- specifically because it needs to work BEFORE email verification — at that point the app
-- has no authenticated session yet, so a client-side insert governed by the RLS policies
-- below wouldn't be able to satisfy an "owner can insert their own row" check anyway.
create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    insert into public.profiles (id, username, email)
    values (
        new.id,
        coalesce(new.raw_user_meta_data->>'username', split_part(new.email, '@', 1)),
        new.email
    );
    return new;
end;
$$;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created
    after insert on auth.users
    for each row execute function public.handle_new_user();

-- 3. Row Level Security — signed-in users can read and update their own row, and nobody
-- else's. (Reading OTHER users' profiles — e.g. for a future friends list — would need its
-- own, more permissive policy; not needed for what's built so far.)
alter table public.profiles enable row level security;

drop policy if exists "Users can view their own profile" on public.profiles;
create policy "Users can view their own profile"
    on public.profiles for select
    using (auth.uid() = id);

drop policy if exists "Users can update their own profile" on public.profiles;
create policy "Users can update their own profile"
    on public.profiles for update
    using (auth.uid() = id);

-- 4. Narrow, security-definer functions for the two lookups the app needs to make WITHOUT
-- an authenticated session (checking username availability at registration, and resolving
-- a username to an email at login) — deliberately NOT a public SELECT policy on the whole
-- table, which would expose every user's email address to anyone holding the anon key.
create or replace function public.username_exists(lookup_username text)
returns boolean
language sql
security definer
set search_path = public
as $$
    select exists(select 1 from public.profiles where username = lookup_username);
$$;

create or replace function public.get_email_for_username(lookup_username text)
returns text
language sql
security definer
set search_path = public
as $$
    select email from public.profiles where username = lookup_username limit 1;
$$;

grant execute on function public.username_exists(text) to anon, authenticated;
grant execute on function public.get_email_for_username(text) to anon, authenticated;

-- 5. To make yourself (or anyone) a beta tester with lifetime Pro access, run:
--
--   update public.profiles set is_beta_tester = true where username = 'their_username';
--
-- LicenseService treats is_beta_tester = true as fully unlocking Pro regardless of the
-- license column, so this alone is enough — no need to also change `license`.

-- 6. Self-service account deletion.
--
-- A desktop client can never safely hold the credentials needed to hard-delete an auth.users
-- row (that requires the service_role key via Supabase's admin API, which must never ship in
-- a client app). What the client CAN safely do — because it's scoped to the caller's own row
-- by RLS — is flag its own account for deletion. Actually purging auth.users + this row still
-- needs a trusted server-side process (e.g. a scheduled Supabase Edge Function) that finds
-- profiles where deletion_requested_at is old enough and calls the admin API; that scheduled
-- job is out of scope here, same as cloud sync.
alter table public.profiles add column if not exists deletion_requested_at timestamptz;

create or replace function public.request_self_deletion()
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    update public.profiles set deletion_requested_at = now() where id = auth.uid();
end;
$$;

grant execute on function public.request_self_deletion() to authenticated;

-- 7. In-app email verification / password reset (numeric code, not a browser link).
--
-- The C# client calls POST /auth/v1/verify with {type, email, token} to exchange a numeric
-- code for a session, for both signup confirmation and password recovery. By default Supabase's
-- email templates only include a {{ .ConfirmationURL }} link, not the bare token — for the
-- code-entry screens in the app to work, go to:
--
--   Supabase Dashboard → Authentication → Email Templates
--
-- ...and add {{ .Token }} to the body of both the "Confirm signup" and "Reset Password"
-- templates (Supabase sends both the link and the token in the same email; the app only
-- needs the token). No further backend change is required — /auth/v1/verify already accepts
-- token-based verification out of the box.

-- 8. Account types: suspension + an in-app admin panel.
--
-- Suspension is enforced client-side, in SupabaseAuthService.GetProfileAsync — the app checks
-- this flag every time it fetches a profile (login, startup auto-login, and a periodic
-- revalidation while the app is running) and refuses to sign the user in if it's set. This is a
-- deliberate, documented limitation, same spirit as request_self_deletion() above: a modified
-- client could skip this check. Real airtight enforcement needs a Supabase "Before Sign-In"
-- Auth Hook (Authentication -> Hooks in the dashboard), which is out of scope here.
alter table public.profiles add column if not exists is_suspended boolean not null default false;

-- Shared by both admin functions below — true only for the CALLER's own account, checked via
-- auth.uid() (never trust a user-supplied id for "am I an admin" checks).
create or replace function public.is_caller_admin()
returns boolean
language sql
security definer
set search_path = public
as $$
    select exists(select 1 from public.profiles where id = auth.uid() and license = 'Administrator');
$$;

-- Lists every user for the admin panel. Deliberately NOT a public/authenticated SELECT policy
-- on profiles (that would let any signed-in user read everyone else's data) — this function is
-- the only way to see other users' rows, and it refuses outright if the caller isn't an admin.
-- Also pulls email_confirmed_at/last_sign_in_at from auth.users, which isn't in public.profiles
-- at all and can only be read by a security-definer function like this one.
create or replace function public.admin_list_users()
returns table (
    user_id uuid,
    username text,
    email text,
    license text,
    is_beta_tester boolean,
    is_suspended boolean,
    email_verified boolean,
    created_at timestamptz,
    last_login_at timestamptz
)
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    return query
        select p.id, p.username, p.email, p.license, p.is_beta_tester, p.is_suspended,
               (u.email_confirmed_at is not null), p.created_at, u.last_sign_in_at
        from public.profiles p
        join auth.users u on u.id = p.id
        order by p.created_at desc;
end;
$$;

-- Lets an admin change another user's license/beta/suspended status. Deliberately narrow —
-- never touches email/username, so this can't be used to take over someone's account.
create or replace function public.admin_update_user(
    target_user_id uuid,
    new_license text,
    new_is_beta_tester boolean,
    new_is_suspended boolean
)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    update public.profiles
    set license = new_license,
        is_beta_tester = new_is_beta_tester,
        is_suspended = new_is_suspended
    where id = target_user_id;
end;
$$;

-- Grants here only allow an authenticated user to CALL these functions at all — the
-- is_caller_admin() check inside each is what actually restricts what they can do, same
-- pattern as request_self_deletion() above.
grant execute on function public.is_caller_admin() to authenticated;
grant execute on function public.admin_list_users() to authenticated;
grant execute on function public.admin_update_user(uuid, text, boolean, boolean) to authenticated;

-- To bootstrap your very first Administrator account (there's no admin panel yet to grant this
-- through, since no admin exists), run:
--
--   update public.profiles set license = 'Administrator' where username = 'their_username';
--
-- After that, use the in-app Admin Panel (Account window -> Admin Panel, visible only to
-- Administrator accounts) to manage everyone else.

-- 9. Cloud sync: settings + sound library METADATA only (favorites/tags/folders/names) — never
-- the actual audio files themselves, so this needs no Supabase Storage bucket, just Postgres.
-- One row per user; settings and library are versioned independently (by their own
-- *_updated_at) so syncing one doesn't require re-uploading the other.
create table if not exists public.cloud_sync (
    user_id uuid primary key references auth.users(id) on delete cascade,
    settings_json jsonb,
    settings_updated_at timestamptz,
    library_json jsonb,
    library_updated_at timestamptz
);

alter table public.cloud_sync enable row level security;

drop policy if exists "Users can view their own sync data" on public.cloud_sync;
create policy "Users can view their own sync data"
    on public.cloud_sync for select
    using (auth.uid() = user_id);

drop policy if exists "Users can insert their own sync data" on public.cloud_sync;
create policy "Users can insert their own sync data"
    on public.cloud_sync for insert
    with check (auth.uid() = user_id);

drop policy if exists "Users can update their own sync data" on public.cloud_sync;
create policy "Users can update their own sync data"
    on public.cloud_sync for update
    using (auth.uid() = user_id);

-- SupabaseCloudService pushes/pulls via PostgREST's upsert (POST with
-- "Prefer: resolution=merge-duplicates") and a plain GET filtered to the caller's own row —
-- both already satisfied by the RLS policies above, no extra RPC needed here.

-- 10. Plugin Marketplace trust/verification.
--
-- The app's Plugin Marketplace is a fixed local catalog (PluginCatalog.cs) of built-in feature
-- toggles, not a real third-party plugin loader — nothing here can execute arbitrary code. This
-- table exists so an admin can mark which of those catalog entries are "verified" trustworthy,
-- and have every user's Marketplace (including offline/not-logged-in users, since this is
-- read with just the anon key) see the same live status rather than something baked into the
-- app binary. plugin_id is a plain string (PluginCatalog's ids), not a uuid like the other
-- tables here, since it's referencing static catalog entries, not auth.users rows.
create table if not exists public.plugin_trust (
    plugin_id text primary key,
    is_verified boolean not null default false,
    verified_by uuid references auth.users(id),
    verified_at timestamptz
);

alter table public.plugin_trust enable row level security;

-- Publicly readable — trust status isn't sensitive, and it needs to be visible even to users
-- who aren't logged in at all (the app's offline mode is a first-class path).
drop policy if exists "Anyone can view plugin trust status" on public.plugin_trust;
create policy "Anyone can view plugin trust status"
    on public.plugin_trust for select
    using (true);

-- Same shape as admin_update_user above — security-definer, gated by is_caller_admin(), and an
-- upsert so the first time a given plugin_id is verified doesn't need a separate seed row.
create or replace function public.admin_set_plugin_verified(target_plugin_id text, new_is_verified boolean)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    insert into public.plugin_trust (plugin_id, is_verified, verified_by, verified_at)
    values (target_plugin_id, new_is_verified, auth.uid(), now())
    on conflict (plugin_id) do update
        set is_verified = excluded.is_verified,
            verified_by = excluded.verified_by,
            verified_at = excluded.verified_at;
end;
$$;

grant execute on function public.admin_set_plugin_verified(text, boolean) to authenticated;

-- 11. Community script plugins.
--
-- User-authored plugins are small scripts run through Jint (a sandboxed JS interpreter — see
-- PluginScriptRunner.cs), NOT compiled code — the app never loads or executes arbitrary .NET
-- assemblies. script_source is plain, short, human-readable text, which is what makes admin
-- verification here an honest claim (an admin can actually read it) rather than the empty
-- promise "verifying" a compiled binary would be.
create table if not exists public.community_plugins (
    id uuid primary key default gen_random_uuid(),
    name text not null,
    description text,
    author_username text not null,
    submitted_by uuid references auth.users(id) on delete cascade,
    script_source text not null,
    is_verified boolean not null default false,
    verified_by uuid references auth.users(id),
    verified_at timestamptz,
    created_at timestamptz not null default now()
);

alter table public.community_plugins enable row level security;

-- Publicly readable — browsing/searching the Community tab doesn't require being logged in,
-- same reasoning as plugin_trust above.
drop policy if exists "Anyone can view community plugins" on public.community_plugins;
create policy "Anyone can view community plugins"
    on public.community_plugins for select
    using (true);

-- Submitting requires being logged in, and only as yourself.
drop policy if exists "Users can submit their own plugin" on public.community_plugins;
create policy "Users can submit their own plugin"
    on public.community_plugins for insert
    with check (auth.uid() = submitted_by);

-- author_username is never trusted from the client — without this, a client could send any
-- name it wants and impersonate someone else's authorship. This always overwrites it with the
-- caller's own profile username, regardless of what was submitted in the insert payload.
create or replace function public.set_community_plugin_author()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    new.author_username := (select username from public.profiles where id = auth.uid());
    new.submitted_by := auth.uid();
    return new;
end;
$$;

drop trigger if exists on_community_plugin_insert on public.community_plugins;
create trigger on_community_plugin_insert
    before insert on public.community_plugins
    for each row execute function public.set_community_plugin_author();

create or replace function public.admin_set_community_plugin_verified(target_plugin_id uuid, new_is_verified boolean)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    update public.community_plugins
    set is_verified = new_is_verified, verified_by = auth.uid(), verified_at = now()
    where id = target_plugin_id;
end;
$$;

-- Outright removal for spam/malicious submissions — moderation needs more than just "unverify".
create or replace function public.admin_delete_community_plugin(target_plugin_id uuid)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    delete from public.community_plugins where id = target_plugin_id;
end;
$$;

grant execute on function public.admin_set_community_plugin_verified(uuid, boolean) to authenticated;
grant execute on function public.admin_delete_community_plugin(uuid) to authenticated;

-- 12. Community plugin packs — the "Basic Plugin" (settings-pack, no code) equivalent of
-- community_plugins above. Same shape/conventions, just pack_json (a serialized PluginPack:
-- hotkeys/voice changer presets/theme) instead of script_source.
create table if not exists public.community_packs (
    id uuid primary key default gen_random_uuid(),
    name text not null,
    description text,
    author_username text not null,
    submitted_by uuid references auth.users(id) on delete cascade,
    pack_json jsonb not null,
    is_verified boolean not null default false,
    verified_by uuid references auth.users(id),
    verified_at timestamptz,
    created_at timestamptz not null default now()
);

alter table public.community_packs enable row level security;

drop policy if exists "Anyone can view community packs" on public.community_packs;
create policy "Anyone can view community packs"
    on public.community_packs for select
    using (true);

drop policy if exists "Users can submit their own pack" on public.community_packs;
create policy "Users can submit their own pack"
    on public.community_packs for insert
    with check (auth.uid() = submitted_by);

create or replace function public.set_community_pack_author()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    new.author_username := (select username from public.profiles where id = auth.uid());
    new.submitted_by := auth.uid();
    return new;
end;
$$;

drop trigger if exists on_community_pack_insert on public.community_packs;
create trigger on_community_pack_insert
    before insert on public.community_packs
    for each row execute function public.set_community_pack_author();

create or replace function public.admin_set_community_pack_verified(target_pack_id uuid, new_is_verified boolean)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    update public.community_packs
    set is_verified = new_is_verified, verified_by = auth.uid(), verified_at = now()
    where id = target_pack_id;
end;
$$;

create or replace function public.admin_delete_community_pack(target_pack_id uuid)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    delete from public.community_packs where id = target_pack_id;
end;
$$;

grant execute on function public.admin_set_community_pack_verified(uuid, boolean) to authenticated;
grant execute on function public.admin_delete_community_pack(uuid) to authenticated;

-- 13. Admin broadcast message — a single, editable announcement shown to every user (including
-- offline/not-logged-in, same public-read reasoning as plugin_trust). Singleton table (id is
-- always 1) rather than a log of messages — admins overwrite the one current announcement rather
-- than managing a list.
create table if not exists public.admin_message (
    id integer primary key default 1,
    message text not null default '',
    updated_by uuid references auth.users(id),
    updated_at timestamptz,
    constraint admin_message_singleton check (id = 1)
);

insert into public.admin_message (id, message)
values (1, '')
on conflict (id) do nothing;

alter table public.admin_message enable row level security;

drop policy if exists "Anyone can view the admin message" on public.admin_message;
create policy "Anyone can view the admin message"
    on public.admin_message for select
    using (true);

create or replace function public.admin_set_message(new_message text)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    update public.admin_message
    set message = new_message, updated_by = auth.uid(), updated_at = now()
    where id = 1;
end;
$$;

grant execute on function public.admin_set_message(text) to authenticated;

-- 14. Content reports — lets a signed-in user flag a Community Plugin or Community Pack for
-- admin review. Complements the profanity filter (catches things text matching can't, like a
-- working-but-malicious script) and the verification checkmark — a report can be filed against
-- already-verified content too. Not publicly readable (would leak who reported what to anyone);
-- admins read/act on reports only via the admin_list_reports()/admin_set_report_status() RPCs
-- below, same is_caller_admin() gate used everywhere else. content_name/reporter_username are
-- snapshots taken at report time so a report still makes sense to an admin even if the reported
-- content or the reporter's profile changes later.
create table if not exists public.content_reports (
    id uuid primary key default gen_random_uuid(),
    content_type text not null check (content_type in ('plugin', 'pack')),
    content_id uuid not null,
    content_name text not null,
    reporter_id uuid references auth.users(id),
    reporter_username text,
    reason text not null,
    status text not null default 'open' check (status in ('open', 'dismissed', 'resolved')),
    created_at timestamptz not null default now()
);

alter table public.content_reports enable row level security;

-- No select policy at all — rows are only ever readable via the security definer RPC below,
-- which enforces the admin check itself. A reporter can't even read their own submitted reports
-- back; nothing needs to.
drop policy if exists "Authenticated users can submit reports" on public.content_reports;
create policy "Authenticated users can submit reports"
    on public.content_reports for insert
    with check (auth.uid() = reporter_id);

create or replace function public.set_content_report_reporter()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    new.reporter_id := auth.uid();
    new.reporter_username := (select username from public.profiles where id = auth.uid());
    return new;
end;
$$;

drop trigger if exists on_content_report_insert on public.content_reports;
create trigger on_content_report_insert
    before insert on public.content_reports
    for each row execute function public.set_content_report_reporter();

create or replace function public.admin_list_reports()
returns setof public.content_reports
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    return query select * from public.content_reports order by created_at desc;
end;
$$;

create or replace function public.admin_set_report_status(target_report_id uuid, new_status text)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;
    if new_status not in ('open', 'dismissed', 'resolved') then
        raise exception 'Invalid status';
    end if;

    update public.content_reports set status = new_status where id = target_report_id;
end;
$$;

grant execute on function public.admin_list_reports() to authenticated;
grant execute on function public.admin_set_report_status(uuid, text) to authenticated;

-- 15. Self-serve beta enrollment, called by the "Join the Beta" button on the marketing
-- website (SonarWebsite/js/api.js). Deliberately a narrow RPC rather than letting the
-- website PATCH profiles.is_beta_tester directly — the "update own profile" RLS policy in
-- section 3 only checks row ownership (auth.uid() = id), not which columns changed, so a raw
-- PATCH from a browser would technically also be able to touch license/is_suspended for a
-- determined attacker. This function can only ever set is_beta_tester = true on the CALLER's
-- own row, nothing else — same shape as request_self_deletion() above.
create or replace function public.join_beta()
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    update public.profiles set is_beta_tester = true where id = auth.uid();
end;
$$;

grant execute on function public.join_beta() to authenticated;

-- 16. Hardens the "Users can update their own profile" policy from section 3.
--
-- Postgres RLS is row-scoped only — it has no concept of "which columns changed" — so on its
-- own, that policy lets a signed-in user PATCH ANY column on their own row via a raw PostgREST
-- request, not just the ones the app's UI exposes. SupabaseAuthService.UpdateProfileAsync only
-- ever sends display_name/country/language/cloud_enabled, but nothing server-side stopped a
-- modified client from sending license/is_beta_tester/is_suspended directly, using the app's
-- own (intentionally public) anon key plus that user's own valid access token. Column-level
-- GRANTs close that gap: license/is_beta_tester/is_suspended/deletion_requested_at can now only
-- be changed through the existing SECURITY DEFINER RPCs (admin_update_user, join_beta,
-- request_self_deletion), which each independently re-check who's allowed to call them.
--
-- Safe to run even on a fresh project (before any REVOKE/GRANT has ever applied) — Supabase's
-- default schema privileges grant ALL on public tables to anon/authenticated, so the REVOKE
-- below always has something to remove.
revoke update on public.profiles from authenticated, anon;
grant update (display_name, country, language, cloud_enabled) on public.profiles to authenticated;

-- 17. Support tickets — lets a signed-in user submit a help request from inside the app and
-- have a real back-and-forth conversation with an admin, not just one reply. A ticket
-- (support_tickets) is just the thread's metadata (subject/status); the actual conversation
-- lives in support_ticket_messages, one row per message from either side. Supersedes an earlier
-- single-reply version of this table — the cleanup statements below make re-running this
-- section safe whether or not that version was ever applied.
drop function if exists public.admin_reply_to_ticket(uuid, text, text);
drop trigger if exists on_support_ticket_insert on public.support_tickets;
drop function if exists public.set_support_ticket_author();
drop policy if exists "Users can submit their own ticket" on public.support_tickets;

create table if not exists public.support_tickets (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null references auth.users(id) on delete cascade,
    username text,
    subject text not null,
    status text not null default 'open' check (status in ('open', 'in_progress', 'resolved')),
    created_at timestamptz not null default now()
);

-- Columns from the single-reply version of this feature, if it was already run — the
-- conversation now lives in support_ticket_messages instead.
alter table public.support_tickets drop column if exists message;
alter table public.support_tickets drop column if exists reply;
alter table public.support_tickets drop column if exists replied_by;
alter table public.support_tickets drop column if exists replied_at;

alter table public.support_tickets enable row level security;

drop policy if exists "Users can view their own tickets" on public.support_tickets;
create policy "Users can view their own tickets"
    on public.support_tickets for select
    using (auth.uid() = user_id);

-- No insert/update policy on this table — tickets are only ever created via
-- create_support_ticket() and status only ever changes via send_ticket_message()/
-- admin_send_ticket_message() below, so every write goes through a function that can enforce
-- the right side effects (creating the first message, reopening on reply) atomically.

create table if not exists public.support_ticket_messages (
    id uuid primary key default gen_random_uuid(),
    ticket_id uuid not null references public.support_tickets(id) on delete cascade,
    sender_id uuid references auth.users(id),
    sender_username text,
    is_admin boolean not null default false,
    body text not null,
    created_at timestamptz not null default now()
);

alter table public.support_ticket_messages enable row level security;

drop policy if exists "Users can view messages on their own tickets" on public.support_ticket_messages;
create policy "Users can view messages on their own tickets"
    on public.support_ticket_messages for select
    using (exists (
        select 1 from public.support_tickets t
        where t.id = ticket_id and t.user_id = auth.uid()
    ));

-- No insert policy here either — every message, from either side, goes through one of the
-- three security definer functions below, so sender_id/sender_username/is_admin can never be
-- spoofed by the client.

create or replace function public.create_support_ticket(subject_text text, body_text text)
returns uuid
language plpgsql
security definer
set search_path = public
as $$
declare
    new_ticket_id uuid;
    sender_name text;
begin
    select username into sender_name from public.profiles where id = auth.uid();

    insert into public.support_tickets (user_id, username, subject, status)
    values (auth.uid(), sender_name, subject_text, 'open')
    returning id into new_ticket_id;

    insert into public.support_ticket_messages (ticket_id, sender_id, sender_username, is_admin, body)
    values (new_ticket_id, auth.uid(), sender_name, false, body_text);

    return new_ticket_id;
end;
$$;

create or replace function public.send_ticket_message(target_ticket_id uuid, body_text text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    ticket_owner uuid;
    ticket_status text;
    sender_name text;
begin
    select user_id, status into ticket_owner, ticket_status from public.support_tickets where id = target_ticket_id;
    if ticket_owner is null or ticket_owner != auth.uid() then
        raise exception 'Not authorized';
    end if;
    -- A resolved ticket is closed for the user — the client already hides the reply box for a
    -- resolved ticket, this is just the server-side backstop. They start a new request instead
    -- of reopening this one.
    if ticket_status = 'resolved' then
        raise exception 'This request is resolved. Start a new request if you need more help.';
    end if;

    select username into sender_name from public.profiles where id = auth.uid();

    insert into public.support_ticket_messages (ticket_id, sender_id, sender_username, is_admin, body)
    values (target_ticket_id, auth.uid(), sender_name, false, body_text);
end;
$$;

create or replace function public.admin_list_support_tickets()
returns setof public.support_tickets
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    return query select * from public.support_tickets order by created_at desc;
end;
$$;

create or replace function public.admin_list_ticket_messages(target_ticket_id uuid)
returns setof public.support_ticket_messages
language plpgsql
security definer
set search_path = public
as $$
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;

    return query
        select * from public.support_ticket_messages
        where ticket_id = target_ticket_id
        order by created_at asc;
end;
$$;

create or replace function public.admin_send_ticket_message(target_ticket_id uuid, body_text text, new_status text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    sender_name text;
begin
    if not public.is_caller_admin() then
        raise exception 'Not authorized';
    end if;
    if new_status not in ('open', 'in_progress', 'resolved') then
        raise exception 'Invalid status';
    end if;

    select username into sender_name from public.profiles where id = auth.uid();

    insert into public.support_ticket_messages (ticket_id, sender_id, sender_username, is_admin, body)
    values (target_ticket_id, auth.uid(), sender_name, true, body_text);

    update public.support_tickets set status = new_status where id = target_ticket_id;
end;
$$;

grant execute on function public.create_support_ticket(text, text) to authenticated;
grant execute on function public.send_ticket_message(uuid, text) to authenticated;
grant execute on function public.admin_list_support_tickets() to authenticated;
grant execute on function public.admin_list_ticket_messages(uuid) to authenticated;

-- 18. Stripe: real Pro purchases (one-time, lifetime — not a subscription). Written only by the
-- website's stripe-webhook Netlify Function, using the Supabase service_role key, which bypasses
-- RLS/column-GRANTs entirely by design — so unlike every other column added to this table, no
-- extra REVOKE/GRANT is needed here: section 16's grant is an explicit allowlist that doesn't
-- include these, so they're already unwritable by any authenticated/anon client by default.
alter table public.profiles add column if not exists stripe_customer_id text;
alter table public.profiles add column if not exists pro_purchased_at timestamptz;
grant execute on function public.admin_send_ticket_message(uuid, text, text) to authenticated;
