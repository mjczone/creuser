import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

// Mock the SDK before importing the store. The store closes over the
// Workspaces import at module-load time, so the mock has to be set up before
// the dynamic import below resolves.
const getWorkspaceMock = vi.fn();
vi.mock('src/api', () => ({
  Workspaces: {
    getWorkspace: (...args: unknown[]) => getWorkspaceMock(...args) as unknown,
  },
}));

import { useWorkspaceStore } from 'stores/workspace';

interface WorkspaceFixture {
  workspaceId: string;
  slug: string;
  name: string;
  type: string;
}

function fixtureWorkspace(slug: string): WorkspaceFixture {
  return {
    workspaceId: '00000000-0000-0000-0000-000000000001',
    slug,
    name: `Workspace ${slug}`,
    type: 'git',
  };
}

describe('useWorkspaceStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    getWorkspaceMock.mockReset();
  });

  it('starts empty — get() returns null for any slug', () => {
    const store = useWorkspaceStore();
    expect(store.get('anything')).toBeNull();
    expect(store.isDenied('anything')).toBe(false);
  });

  it('ensureLoaded fetches once and caches the result', async () => {
    const ws = fixtureWorkspace('compas');
    getWorkspaceMock.mockResolvedValueOnce({
      data: { result: ws },
      error: null,
    });
    const store = useWorkspaceStore();

    const first = await store.ensureLoaded('compas');
    expect(first).toEqual(ws);
    expect(getWorkspaceMock).toHaveBeenCalledTimes(1);

    // Second call returns the same cached record without re-fetching.
    const second = await store.ensureLoaded('compas');
    expect(second).toEqual(ws);
    expect(getWorkspaceMock).toHaveBeenCalledTimes(1);
  });

  it('ensureLoaded marks slug denied on 4xx and never re-fetches', async () => {
    getWorkspaceMock.mockResolvedValueOnce({
      data: null,
      error: { status: 403, title: 'Forbidden' },
    });
    const store = useWorkspaceStore();

    const first = await store.ensureLoaded('forbidden-slug');
    expect(first).toBeNull();
    expect(store.isDenied('forbidden-slug')).toBe(true);
    expect(getWorkspaceMock).toHaveBeenCalledTimes(1);

    // Subsequent call short-circuits — no second network hit.
    const second = await store.ensureLoaded('forbidden-slug');
    expect(second).toBeNull();
    expect(getWorkspaceMock).toHaveBeenCalledTimes(1);
  });

  it('ensureLoaded denies on thrown errors too', async () => {
    getWorkspaceMock.mockRejectedValueOnce(new Error('network down'));
    const store = useWorkspaceStore();

    const result = await store.ensureLoaded('flaky-slug');
    expect(result).toBeNull();
    expect(store.isDenied('flaky-slug')).toBe(true);
  });

  it('ensureLoaded handles concurrent callers without double-fetching', async () => {
    const ws = fixtureWorkspace('shared');
    let resolveFetch!: (value: { data: { result: WorkspaceFixture }; error: null }) => void;
    getWorkspaceMock.mockImplementation(
      () =>
        new Promise((res) => {
          resolveFetch = res;
        }),
    );
    const store = useWorkspaceStore();

    const aPromise = store.ensureLoaded('shared');
    const bPromise = store.ensureLoaded('shared');
    // Resolve once — the second caller polls until the cache populates.
    resolveFetch({ data: { result: ws }, error: null });

    const [a, b] = await Promise.all([aPromise, bPromise]);
    expect(a).toEqual(ws);
    expect(b).toEqual(ws);
    // Implementation calls the SDK exactly once for the first caller; the
    // second caller waits for the cache to populate rather than firing a
    // duplicate request.
    expect(getWorkspaceMock).toHaveBeenCalledTimes(1);
  });

  it('upsert primes the cache without going through ensureLoaded', () => {
    const ws = fixtureWorkspace('seeded');
    const store = useWorkspaceStore();

    store.upsert(ws as never); // store types accept WorkspaceResult
    expect(store.get('seeded')).toEqual(ws);
  });

  it('reset clears cache + denied + in-flight tracking', async () => {
    const ws = fixtureWorkspace('reset-me');
    getWorkspaceMock.mockResolvedValueOnce({
      data: { result: ws },
      error: null,
    });
    const store = useWorkspaceStore();
    await store.ensureLoaded('reset-me');
    await store.ensureLoaded('denied-too').catch(() => null);
    getWorkspaceMock.mockResolvedValueOnce({
      data: null,
      error: { status: 404 },
    });
    await store.ensureLoaded('denied-too');

    expect(store.get('reset-me')).toEqual(ws);

    store.reset();

    expect(store.get('reset-me')).toBeNull();
    expect(store.isDenied('denied-too')).toBe(false);
  });
});
