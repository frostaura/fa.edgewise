import { useState } from 'react'
import { useNavigate } from 'react-router'
import { BellIcon, EyeIcon, PlusIcon, Trash2Icon } from 'lucide-react'
import { toast } from 'sonner'

import {
  useAddWatchlistItemMutation,
  useCreateWatchlistMutation,
  useDeleteWatchlistItemMutation,
  useDeleteWatchlistMutation,
  useGetInstrumentsQuery,
  useGetWatchlistsQuery,
  usePromoteWatchlistItemMutation,
  type WatchlistItem,
} from '@/api/radarApi'
import { getApiErrorMessage } from '@/api/types'
import { EmptyState } from '@/components/domain/EmptyState'
import { StaleBadge } from '@/components/domain/StaleBadge'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
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
import { Textarea } from '@/components/ui/textarea'

/** Watchlist cards: instrument, quote (stale-flagged), why-watching, promote-to-plan. */
export function WatchlistsSection() {
  const { data: lists, isLoading } = useGetWatchlistsQuery()
  const [createList] = useCreateWatchlistMutation()
  const [deleteList] = useDeleteWatchlistMutation()
  const [newName, setNewName] = useState('')

  const addList = async () => {
    const name = newName.trim()
    if (!name) return
    try {
      await createList({ name }).unwrap()
      setNewName('')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not create watchlist.'))
    }
  }

  if (isLoading) {
    return <Skeleton className="h-40 rounded-xl" />
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <Input
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && addList()}
          placeholder="New watchlist name"
          className="max-w-56"
        />
        <Button size="sm" onClick={addList} disabled={!newName.trim()}>
          <PlusIcon aria-hidden />
          Add list
        </Button>
      </div>

      {!lists || lists.length === 0 ? (
        <EmptyState
          icon={EyeIcon}
          title="No watchlists yet"
          hint="Create a list and add the instruments you're stalking, with a note on why."
        />
      ) : (
        lists.map((list) => (
          <Card key={list.id} className="py-4">
            <CardHeader className="flex flex-row items-center justify-between px-4 py-0">
              <CardTitle className="text-base">{list.name}</CardTitle>
              <div className="flex items-center gap-2">
                <AddItemDialog watchlistId={list.id} />
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label={`Delete ${list.name}`}
                  onClick={() => deleteList(list.id)}
                >
                  <Trash2Icon aria-hidden />
                </Button>
              </div>
            </CardHeader>
            <CardContent className="flex flex-col gap-2 px-4">
              {list.items.length === 0 ? (
                <p className="text-sm text-muted-foreground">No instruments yet.</p>
              ) : (
                list.items.map((item) => <WatchlistItemRow key={item.id} item={item} />)
              )}
            </CardContent>
          </Card>
        ))
      )}
    </div>
  )
}

function WatchlistItemRow({ item }: { item: WatchlistItem }) {
  const navigate = useNavigate()
  const [promote] = usePromoteWatchlistItemMutation()
  const [deleteItem] = useDeleteWatchlistItemMutation()

  const promoteToPlan = async () => {
    try {
      const payload = await promote(item.id).unwrap()
      const params = new URLSearchParams({ instrumentId: payload.instrumentId })
      if (payload.note) params.set('note', payload.note)
      navigate(`/journal/plans/new?${params.toString()}`)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not promote to plan.'))
    }
  }

  return (
    <div className="flex flex-wrap items-center gap-3 rounded-lg border p-3">
      <div className="flex min-w-32 flex-col">
        <span className="font-semibold">{item.symbol ?? 'Unknown'}</span>
        <span className="text-xs text-muted-foreground">{item.instrumentName}</span>
      </div>
      <div className="flex min-w-28 flex-col">
        {item.quote ? (
          <>
            <span className="tabular-nums">{item.quote.price}</span>
            {item.quote.stale && <StaleBadge updatedAt={item.quote.asOf} />}
          </>
        ) : (
          <span className="text-sm text-muted-foreground">no quote</span>
        )}
      </div>
      <div className="min-w-0 flex-1">
        {item.whyWatching && <p className="truncate text-sm">{item.whyWatching}</p>}
        {item.note && <p className="truncate text-xs text-muted-foreground">{item.note}</p>}
      </div>
      <Badge variant="outline" className="gap-1">
        <BellIcon className="size-3" aria-hidden />
        {item.alertCount}
      </Badge>
      <div className="flex items-center gap-1">
        <Button size="sm" variant="secondary" onClick={promoteToPlan}>
          Promote to plan
        </Button>
        <Button
          variant="ghost"
          size="icon"
          aria-label="Remove from watchlist"
          onClick={() => deleteItem(item.id)}
        >
          <Trash2Icon aria-hidden />
        </Button>
      </div>
    </div>
  )
}

function AddItemDialog({ watchlistId }: { watchlistId: string }) {
  const [open, setOpen] = useState(false)
  const [instrumentId, setInstrumentId] = useState('')
  const [whyWatching, setWhyWatching] = useState('')
  const [note, setNote] = useState('')
  const { data: instruments, isError } = useGetInstrumentsQuery()
  const [addItem, { isLoading }] = useAddWatchlistItemMutation()

  const submit = async () => {
    try {
      await addItem({
        watchlistId,
        instrumentId,
        whyWatching: whyWatching.trim() || undefined,
        note: note.trim() || undefined,
      }).unwrap()
      setOpen(false)
      setInstrumentId('')
      setWhyWatching('')
      setNote('')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not add instrument.'))
    }
  }

  return (
    <>
      <Button size="sm" variant="outline" onClick={() => setOpen(true)}>
        <PlusIcon aria-hidden />
        Add
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add instrument</DialogTitle>
          </DialogHeader>
          <div className="flex flex-col gap-3">
            {isError || !instruments || instruments.length === 0 ? (
              <div className="flex flex-col gap-2">
                <Label htmlFor="wl-instrument-id">Instrument id</Label>
                <Input
                  id="wl-instrument-id"
                  value={instrumentId}
                  onChange={(e) => setInstrumentId(e.target.value)}
                  placeholder="Instrument UUID (instrument search unavailable)"
                />
              </div>
            ) : (
              <Select value={instrumentId} onValueChange={setInstrumentId}>
                <SelectTrigger>
                  <SelectValue placeholder="Choose an instrument" />
                </SelectTrigger>
                <SelectContent>
                  {instruments.map((i) => (
                    <SelectItem key={i.id} value={i.id}>
                      {i.symbol} — {i.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
            <div className="flex flex-col gap-2">
              <Label htmlFor="wl-why">Why watching</Label>
              <Textarea
                id="wl-why"
                value={whyWatching}
                onChange={(e) => setWhyWatching(e.target.value)}
                placeholder="The setup you're waiting for"
                rows={2}
              />
            </div>
            <div className="flex flex-col gap-2">
              <Label htmlFor="wl-note">Note</Label>
              <Input
                id="wl-note"
                value={note}
                onChange={(e) => setNote(e.target.value)}
                placeholder="Optional note"
              />
            </div>
          </div>
          <DialogFooter>
            <Button disabled={!instrumentId.trim() || isLoading} onClick={submit}>
              Add to watchlist
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )
}
