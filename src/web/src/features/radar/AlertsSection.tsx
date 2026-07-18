import { useState } from 'react'
import { format } from 'date-fns'
import { BellIcon, BellRingIcon, PlusIcon, Trash2Icon } from 'lucide-react'
import { toast } from 'sonner'

import {
  radarApi,
  useCreateAlertMutation,
  useDeleteAlertMutation,
  useGetAlertsQuery,
  useGetInstrumentsQuery,
  useSubscribePushMutation,
  useUpdateAlertMutation,
  type AlertKind,
} from '@/api/radarApi'
import { getApiErrorMessage } from '@/api/types'
import { useAppDispatch } from '@/app/hooks'
import { EmptyState } from '@/components/domain/EmptyState'
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
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import {
  INSTRUMENT_KINDS,
  KIND_LABELS,
  paramsSummary,
  validateAlertParams,
} from '@/features/radar/alertForms'
import { enablePush, pushSupported } from '@/features/radar/push'

/** Alert manager: table of alerts + create dialog + web-push opt-in. */
export function AlertsSection() {
  const { data: alerts, isLoading } = useGetAlertsQuery()
  const [updateAlert] = useUpdateAlertMutation()
  const [deleteAlert] = useDeleteAlertMutation()

  if (isLoading) {
    return <Skeleton className="h-40 rounded-xl" />
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <CreateAlertDialog />
        <PushOptIn />
      </div>

      {!alerts || alerts.length === 0 ? (
        <EmptyState
          icon={BellIcon}
          title="No alerts yet"
          hint="Create price, zone, sentiment or catalyst alerts — they check every minute and notify in-app, by push and by email."
        />
      ) : (
        <div className="overflow-x-auto rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Kind</TableHead>
                <TableHead>Target</TableHead>
                <TableHead>Params</TableHead>
                <TableHead>Last triggered</TableHead>
                <TableHead className="text-right">Enabled</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {alerts.map((alert) => (
                <TableRow key={alert.id}>
                  <TableCell className="font-medium">{KIND_LABELS[alert.kind]}</TableCell>
                  <TableCell>{alert.symbol ?? '—'}</TableCell>
                  <TableCell className="text-muted-foreground">{paramsSummary(alert)}</TableCell>
                  <TableCell className="text-muted-foreground">
                    {alert.lastTriggeredAt
                      ? format(new Date(alert.lastTriggeredAt), 'd MMM HH:mm')
                      : 'never'}
                  </TableCell>
                  <TableCell className="text-right">
                    <Switch
                      checked={alert.enabled}
                      aria-label={`${KIND_LABELS[alert.kind]} alert enabled`}
                      onCheckedChange={(enabled) =>
                        updateAlert({ id: alert.id, patch: { enabled } })
                      }
                    />
                  </TableCell>
                  <TableCell className="text-right">
                    <Button
                      variant="ghost"
                      size="icon"
                      aria-label="Delete alert"
                      onClick={() => deleteAlert(alert.id)}
                    >
                      <Trash2Icon aria-hidden />
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  )
}

/** "Enable push" opt-in: permission → PushManager.subscribe → POST subscribe. */
function PushOptIn() {
  const dispatch = useAppDispatch()
  const [subscribePush] = useSubscribePushMutation()
  const [busy, setBusy] = useState(false)

  const optIn = async () => {
    setBusy(true)
    try {
      const vapid = await dispatch(radarApi.endpoints.getVapidKey.initiate()).unwrap()
      const result = await enablePush(vapid.publicKey)
      if (!result.ok) {
        toast.error(
          result.reason === 'unsupported'
            ? 'Push is not supported here (needs HTTPS + a service worker).'
            : result.reason === 'denied'
              ? 'Notification permission was denied.'
              : 'Could not subscribe to push.',
        )
        return
      }
      await subscribePush({ subscription: result.subscription }).unwrap()
      toast.success('Push notifications enabled.')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Push is not configured on this server.'))
    } finally {
      setBusy(false)
    }
  }

  if (!pushSupported()) return null

  return (
    <Button size="sm" variant="outline" onClick={optIn} disabled={busy}>
      <BellRingIcon aria-hidden />
      Enable push
    </Button>
  )
}

const KIND_OPTIONS: AlertKind[] = [
  'priceCross',
  'pctMove',
  'zoneTouch',
  'fundingRate',
  'fgExtreme',
  'catalystT24',
]

/** Create-alert dialog with kind-specific parameter forms. */
function CreateAlertDialog() {
  const [open, setOpen] = useState(false)
  const [kind, setKind] = useState<AlertKind>('priceCross')
  const [instrumentId, setInstrumentId] = useState('')
  const [fields, setFields] = useState<Record<string, string>>({})
  const [errors, setErrors] = useState<string[]>([])
  const { data: instruments } = useGetInstrumentsQuery()
  const [createAlert, { isLoading }] = useCreateAlertMutation()

  const setField = (name: string, value: string) =>
    setFields((f) => ({ ...f, [name]: value }))

  const needsInstrument = INSTRUMENT_KINDS.includes(kind)

  const submit = async () => {
    const raw: Record<string, unknown> = { ...defaultsFor(kind), ...prune(fields) }
    const validation = validateAlertParams(kind, raw)
    const errs = [...validation.errors]
    if (needsInstrument && !instrumentId) errs.push('Pick an instrument')
    if (errs.length > 0 || !validation.success) {
      setErrors(errs)
      return
    }
    try {
      await createAlert({
        kind,
        instrumentId: needsInstrument ? instrumentId : undefined,
        params: validation.params,
      }).unwrap()
      toast.success('Alert created.')
      setOpen(false)
      setFields({})
      setErrors([])
    } catch (err) {
      setErrors([getApiErrorMessage(err, 'Could not create the alert.')])
    }
  }

  return (
    <>
      <Button size="sm" onClick={() => setOpen(true)}>
        <PlusIcon aria-hidden />
        New alert
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Create alert</DialogTitle>
            <DialogDescription>
              Checked every minute; you'll be notified in-app (plus push/email when configured).
            </DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-3">
            <div className="flex flex-col gap-2">
              <Label>Kind</Label>
              <Select
                value={kind}
                onValueChange={(v) => {
                  setKind(v as AlertKind)
                  setFields({})
                  setErrors([])
                }}
              >
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {KIND_OPTIONS.map((k) => (
                    <SelectItem key={k} value={k}>
                      {KIND_LABELS[k]}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            {needsInstrument && (
              <div className="flex flex-col gap-2">
                <Label>Instrument</Label>
                {instruments && instruments.length > 0 ? (
                  <Select value={instrumentId} onValueChange={setInstrumentId}>
                    <SelectTrigger>
                      <SelectValue placeholder="Choose an instrument" />
                    </SelectTrigger>
                    <SelectContent>
                      {instruments.map((i) => (
                        <SelectItem key={i.id} value={i.id}>
                          {i.symbol}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                ) : (
                  <Input
                    value={instrumentId}
                    onChange={(e) => setInstrumentId(e.target.value)}
                    placeholder="Instrument UUID"
                  />
                )}
              </div>
            )}

            {kind === 'priceCross' && (
              <div className="grid grid-cols-2 gap-2">
                <div className="flex flex-col gap-2">
                  <Label htmlFor="al-level">Level</Label>
                  <Input
                    id="al-level"
                    inputMode="decimal"
                    value={fields.level ?? ''}
                    onChange={(e) => setField('level', e.target.value)}
                    placeholder="e.g. 65000"
                  />
                </div>
                <div className="flex flex-col gap-2">
                  <Label>Direction</Label>
                  <Select
                    value={fields.direction ?? 'above'}
                    onValueChange={(v) => setField('direction', v)}
                  >
                    <SelectTrigger>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="above">Above</SelectItem>
                      <SelectItem value="below">Below</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
              </div>
            )}

            {kind === 'pctMove' && (
              <div className="grid grid-cols-2 gap-2">
                <div className="flex flex-col gap-2">
                  <Label htmlFor="al-pct">Percent move</Label>
                  <Input
                    id="al-pct"
                    inputMode="decimal"
                    value={fields.pct ?? ''}
                    onChange={(e) => setField('pct', e.target.value)}
                    placeholder="e.g. 5"
                  />
                </div>
                <div className="flex flex-col gap-2">
                  <Label htmlFor="al-window">Window (minutes)</Label>
                  <Input
                    id="al-window"
                    inputMode="numeric"
                    value={fields.windowMinutes ?? '60'}
                    onChange={(e) => setField('windowMinutes', e.target.value)}
                  />
                </div>
              </div>
            )}

            {kind === 'zoneTouch' && (
              <div className="flex flex-col gap-2">
                <Label htmlFor="al-zone">Zone id</Label>
                <Input
                  id="al-zone"
                  value={fields.zoneId ?? ''}
                  onChange={(e) => setField('zoneId', e.target.value)}
                  placeholder="Zone UUID (from an asset's zones)"
                />
              </div>
            )}

            {kind === 'fundingRate' && (
              <div className="flex flex-col gap-2">
                <Label htmlFor="al-threshold">Threshold %</Label>
                <Input
                  id="al-threshold"
                  inputMode="decimal"
                  value={fields.thresholdPct ?? '0.05'}
                  onChange={(e) => setField('thresholdPct', e.target.value)}
                />
                <p className="text-xs text-muted-foreground">
                  Stored now; evaluated once a funding-rate data source is connected.
                </p>
              </div>
            )}

            {kind === 'fgExtreme' && (
              <div className="grid grid-cols-2 gap-2">
                <div className="flex flex-col gap-2">
                  <Label htmlFor="al-min">Fear at or below</Label>
                  <Input
                    id="al-min"
                    inputMode="numeric"
                    value={fields.min ?? '20'}
                    onChange={(e) => setField('min', e.target.value)}
                  />
                </div>
                <div className="flex flex-col gap-2">
                  <Label htmlFor="al-max">Greed at or above</Label>
                  <Input
                    id="al-max"
                    inputMode="numeric"
                    value={fields.max ?? '80'}
                    onChange={(e) => setField('max', e.target.value)}
                  />
                </div>
              </div>
            )}

            {kind === 'catalystT24' && (
              <p className="text-sm text-muted-foreground">
                Fires when a red catalyst enters the next 24 hours for your held or watched
                instruments.
              </p>
            )}

            {errors.length > 0 && (
              <ul className="text-sm text-destructive">
                {errors.map((e) => (
                  <li key={e}>{e}</li>
                ))}
              </ul>
            )}
          </div>
          <DialogFooter>
            <Button disabled={isLoading} onClick={submit}>
              Create alert
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )
}

function defaultsFor(kind: AlertKind): Record<string, unknown> {
  switch (kind) {
    case 'priceCross':
      return { direction: 'above' }
    case 'pctMove':
      return { windowMinutes: 60 }
    case 'fundingRate':
      return { thresholdPct: 0.05 }
    case 'fgExtreme':
      return { min: 20, max: 80 }
    default:
      return {}
  }
}

function prune(fields: Record<string, string>): Record<string, string> {
  return Object.fromEntries(Object.entries(fields).filter(([, v]) => v.trim() !== ''))
}
