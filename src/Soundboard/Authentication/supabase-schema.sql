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
