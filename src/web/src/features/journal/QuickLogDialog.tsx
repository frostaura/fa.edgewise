import * as React from 'react'
import { useNavigate } from 'react-router'
import { toast } from 'sonner'

import { getApiErrorMessage } from '@/api/types'
import {
  useConfessFillMutation,
  useCreateFillMutation,
  useGetPlanLookupsQuery,
  type FillSide,
} from '@/api/journalApi'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'

/** Local date-time value for <input type="datetime-local"> right now. */
function nowLocal(): string {
  const now = new Date()
  now.setSeconds(0, 0)
  const offset = now.getTimezoneOffset()
  return new Date(now.getTime() - offset * 60_000).toISOString().slice(0, 16)
}

export interface QuickLogDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
}

/**
 * Quick Log: the unplanned-trade fast form (instrument, side, qty, price, fees,
 * when). Creates a manual fill and immediately confesses it into an unplanned
 * open trade — the honest path when the buy button was faster than the plan.
 */
export function QuickLogDialog({ open, onOpenChange }: QuickLogDialogProps) {
  const navigate = useNavigate()
  const { data: lookups } = useGetPlanLookupsQuery(undefined, { skip: !open })
  const [createFill, { isLoading: creating }] = useCreateFillMutation()
  const [confessFill, { isLoading: confessing }] = useConfessFillMutation()

  const [instrumentId, setInstrumentId] = React.useState('')
  const [side, setSide] = React.useState<FillSide>('buy')
  const [qty, setQty] = React.useState('')
  const [price, setPrice] = React.useState('')
  const [fee, setFee] = React.useState('')
  const [at, setAt] = React.useState(nowLocal)

  const busy = creating || confessing
  const valid = instrumentId && Number(qty) > 0 && Number(price) > 0

  const submit = async () => {
    if (!valid) return
    try {
      const fill = await createFill({
        instrumentId,
        side,
        qty: Number(qty),
        price: Number(price),
        feeMinor: Math.round(Number(fee || '0') * 100),
        at: new Date(at).toISOString(),
      }).unwrap()
      const confessed = await confessFill(fill.id).unwrap()
      toast.success('Trade logged as unplanned')
      onOpenChange(false)
      if (confessed.tradeId) void navigate(`/journal/trades/${confessed.tradeId}`)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not log the trade'))
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Quick Log</DialogTitle>
          <DialogDescription>
            Log an unplanned fill. It is confessed into the journal so the adherence rubric can
            see it.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="ql-instrument">Instrument</Label>
            <Select value={instrumentId} onValueChange={setInstrumentId}>
              <SelectTrigger id="ql-instrument" className="w-full">
                <SelectValue placeholder="Pick an instrument" />
              </SelectTrigger>
              <SelectContent>
                {(lookups?.instruments ?? []).map((i) => (
                  <SelectItem key={i.id} value={i.id}>
                    {i.symbol} — {i.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div className="flex flex-col gap-1.5">
              <Label>Side</Label>
              <div className="flex gap-1">
                <Button
                  type="button"
                  size="sm"
                  variant={side === 'buy' ? 'default' : 'outline'}
                  className="flex-1"
                  onClick={() => setSide('buy')}
                >
                  Buy
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant={side === 'sell' ? 'default' : 'outline'}
                  className="flex-1"
                  onClick={() => setSide('sell')}
                >
                  Sell
                </Button>
              </div>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="ql-qty">Quantity</Label>
              <Input
                id="ql-qty"
                type="number"
                inputMode="decimal"
                min="0"
                step="any"
                value={qty}
                onChange={(e) => setQty(e.target.value)}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="ql-price">Price</Label>
              <Input
                id="ql-price"
                type="number"
                inputMode="decimal"
                min="0"
                step="any"
                value={price}
                onChange={(e) => setPrice(e.target.value)}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="ql-fee">Fees</Label>
              <Input
                id="ql-fee"
                type="number"
                inputMode="decimal"
                min="0"
                step="any"
                placeholder="0.00"
                value={fee}
                onChange={(e) => setFee(e.target.value)}
              />
            </div>
          </div>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="ql-at">When</Label>
            <Input
              id="ql-at"
              type="datetime-local"
              value={at}
              onChange={(e) => setAt(e.target.value)}
            />
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={busy}>
            Cancel
          </Button>
          <Button onClick={() => void submit()} disabled={!valid || busy}>
            {busy ? 'Logging…' : 'Log trade'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
