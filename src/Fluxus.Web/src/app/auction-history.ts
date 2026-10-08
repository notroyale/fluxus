import { ChangeDetectionStrategy, Component, inject, input, resource, signal } from '@angular/core';
import { OfferTrackingApi } from './offer-tracking-api';
import { date, money, status } from './offer-format';

@Component({
  selector: 'app-auction-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './auction-history.html',
  styleUrl: './auction-history.css',
})
export class AuctionHistory {
  private readonly api = inject(OfferTrackingApi);
  readonly auctionId = input.required<string>();
  readonly observedAt = input.required<string>();
  protected readonly expanded = signal(false);
  protected readonly history = resource({
    params: () =>
      this.expanded() ? { id: this.auctionId(), observedAt: this.observedAt() } : undefined,
    loader: ({ params, abortSignal }) => this.api.history(params.id, abortSignal),
  });
  protected readonly date = date;
  protected readonly money = money;
  protected readonly status = status;
}
