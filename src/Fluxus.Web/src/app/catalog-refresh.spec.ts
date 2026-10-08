import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CatalogRefresh } from './catalog-refresh';
import { parseRefreshStatus, RefreshStatus } from './catalog-refresh-api';

const id = '81258336-bb04-4376-8ec6-2eb30b5734c7';
const idle: RefreshStatus = {
  enabled: true,
  maxAuctions: 100,
  id: null,
  status: 'idle',
  phase: null,
  startedAt: null,
  finishedAt: null,
  retryAt: null,
  auctions: 0,
  items: 0,
  mapped: 0,
  failedAuctions: 0,
  discoveryComplete: null,
  errorCode: null,
};
const running: RefreshStatus = {
  ...idle,
  id,
  status: 'running',
  phase: 'details',
  startedAt: '2026-10-08T10:00:00Z',
};

@Component({
  imports: [CatalogRefresh],
  template: `<app-catalog-refresh (refreshed)="completed.update(increment)" /><span
      class="events"
      >{{ completed() }}</span
    >`,
})
class Host {
  readonly completed = signal(0);
  readonly increment = (value: number) => value + 1;
}

describe('Catalogue refresh', () => {
  let status = idle;
  let postStatus = 202;
  let statusUnavailable = false;
  let posts = 0;
  beforeEach(() => {
    status = { ...idle };
    postStatus = 202;
    statusUnavailable = false;
    posts = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          posts++;
          expect(init.headers).toEqual({ 'X-Fluxus-Refresh': '1' });
          if (postStatus === 202) status = { ...running };
          return new Response(JSON.stringify(status), { status: postStatus });
        }
        return new Response(JSON.stringify(status), { status: statusUnavailable ? 503 : 200 });
      }),
    );
    TestBed.configureTestingModule({ imports: [Host] });
  });
  afterEach(() => vi.unstubAllGlobals());

  it('runs in the background, disables duplicate clicks and emits completion once after a partial run', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    const button = root.querySelector<HTMLButtonElement>('.refresh-button')!;
    expect(button.disabled).toBe(false);
    button.click();
    await fixture.whenStable();
    expect(button.disabled).toBe(true);
    expect(root.textContent).toContain('Tikrinami skelbimai');
    button.click();
    expect(posts).toBe(1);
    status = {
      ...running,
      status: 'completed',
      phase: null,
      auctions: 10,
      items: 12,
      mapped: 11,
      failedAuctions: 1,
      discoveryComplete: false,
      finishedAt: '2026-10-08T10:04:00Z',
    };
    await new Promise((resolve) => setTimeout(resolve, 2_100));
    await fixture.whenStable();
    expect(root.textContent).toContain('10 aukcionų · 12 pasiūlymų · 11 žemėlapyje');
    expect(root.textContent).toContain('1 aukcionų atnaujinti nepavyko');
    expect(root.querySelector('.events')!.textContent).toBe('1');
    root.querySelector<HTMLButtonElement>('.dismiss')!.click();
    await fixture.whenStable();
    expect(root.querySelector('.refresh-panel')).toBeNull();
    fixture.destroy();
  });

  it('shows a rejected start and keeps the saved catalogue independent', async () => {
    postStatus = 429;
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    root.querySelector<HTMLButtonElement>('.refresh-button')!.click();
    await fixture.whenStable();
    expect(root.querySelector('[role="alert"]')!.textContent).toContain('palaukite minutę');
    expect(root.querySelector('.events')!.textContent).toBe('0');
    fixture.destroy();
  });

  it('can recover status after a network failure without starting a second scrape', async () => {
    statusUnavailable = true;
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector<HTMLButtonElement>('.refresh-button')!.disabled).toBe(true);
    statusUnavailable = false;
    status = { ...running };
    root.querySelector<HTMLButtonElement>('.retry')!.click();
    await fixture.whenStable();
    expect(root.textContent).toContain('Atnaujinama');
    expect(posts).toBe(0);
    fixture.destroy();
  });

  it('rejects malformed job state, counts, dates and external error text', () => {
    expect(parseRefreshStatus(idle)).toEqual(idle);
    expect(() => parseRefreshStatus({ ...running, id: null })).toThrow();
    expect(() => parseRefreshStatus({ ...running, auctions: -1 })).toThrow();
    expect(() => parseRefreshStatus({ ...running, mapped: 1, items: 0 })).toThrow();
    expect(() => parseRefreshStatus({ ...running, startedAt: 'yesterday' })).toThrow();
    expect(() => parseRefreshStatus({ ...running, errorCode: 'untrusted error text' })).toThrow();
    expect(() =>
      parseRefreshStatus({ ...running, status: 'completed', finishedAt: null }),
    ).toThrow();
  });
});
