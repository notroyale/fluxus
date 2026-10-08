import {
  ChangeDetectionStrategy,
  Component,
  effect,
  ElementRef,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { ItemCard } from './catalog-api';
import { ShortlistStore } from './shortlist-store';
import { area, date, money, ownership, status } from './offer-format';
import { landscapeLabel } from './landscape-api';
import { OfferChanges } from './offer-changes';

@Component({
  selector: 'app-shortlist',
  imports: [OfferChanges],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shortlist.html',
  styleUrl: './shortlist.css',
})
export class Shortlist {
  protected readonly store = inject(ShortlistStore);
  private readonly panel = viewChild<ElementRef<HTMLDialogElement>>('panel');
  protected readonly open = signal(false);
  readonly now = input.required<number>();
  readonly selected = output<ItemCard>();
  protected readonly area = area;
  protected readonly date = date;
  protected readonly money = money;
  protected readonly ownership = ownership;
  protected readonly status = status;
  protected readonly landscapeLabel = landscapeLabel;
  constructor() {
    effect(() => {
      const panel = this.panel()?.nativeElement;
      if (!panel) return;
      if (this.open() && !panel.open) panel.showModal();
      else if (!this.open() && panel.open) panel.close();
    });
  }
  protected showOffer(card: ItemCard): void {
    this.open.set(false);
    this.selected.emit(card);
  }
}
