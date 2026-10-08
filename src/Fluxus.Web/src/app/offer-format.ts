export function money(value: number | null): string {
  return value === null
    ? 'Nenurodyta'
    : new Intl.NumberFormat('lt-LT', {
        style: 'currency',
        currency: 'EUR',
        maximumFractionDigits: 2,
      }).format(value);
}
export function area(value: number | null): string {
  return value === null
    ? 'Nenurodyta'
    : `${new Intl.NumberFormat('lt-LT', { maximumFractionDigits: 4 }).format(value)} ha`;
}
export function date(value: string | null): string {
  return value
    ? new Intl.DateTimeFormat('lt-LT', {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
        timeZone: 'Europe/Vilnius',
      }).format(new Date(value))
    : 'Nenurodyta';
}
export function status(value: string): string {
  return (
    (
      {
        running: 'Vyksta',
        announced: 'Paskelbta',
        ended: 'Baigta',
        cancelled: 'Atšaukta',
      } as Record<string, string>
    )[value] ?? 'Nežinoma'
  );
}
export function ownership(value: string): string {
  return (
    (
      {
        full: 'Visa nuosavybė',
        partial: 'Nuosavybės dalis',
        unknown: 'Dalis nenurodyta',
        not_applicable: 'Nuosavybė neparduodama',
      } as Record<string, string>
    )[value] ?? 'Dalis nenurodyta'
  );
}
