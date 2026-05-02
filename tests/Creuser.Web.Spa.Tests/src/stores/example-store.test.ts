import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useCounterStore } from 'stores/example-store';

describe('useCounterStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('starts at zero', () => {
    const store = useCounterStore();
    expect(store.counter).toBe(0);
  });

  it('increments', () => {
    const store = useCounterStore();
    store.increment();
    store.increment();
    expect(store.counter).toBe(2);
  });

  it('exposes doubleCount getter', () => {
    const store = useCounterStore();
    store.increment();
    store.increment();
    store.increment();
    expect(store.doubleCount).toBe(6);
  });
});
