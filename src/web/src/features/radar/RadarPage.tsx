import { useSearchParams } from 'react-router'

import { PageHeader } from '@/components/domain/PageHeader'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { AlertsSection } from '@/features/radar/AlertsSection'
import { CalendarSection } from '@/features/radar/CalendarSection'
import { NewsSection } from '@/features/radar/NewsSection'
import { SentimentSection } from '@/features/radar/SentimentSection'
import { WatchlistsSection } from '@/features/radar/WatchlistsSection'

const TABS = ['watchlists', 'calendar', 'news', 'sentiment', 'alerts'] as const
type Tab = (typeof TABS)[number]

/** Radar: watchlists, catalyst calendar, news digest, sentiment, alert manager. */
export default function RadarPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const requested = searchParams.get('tab')
  const tab: Tab = (TABS as readonly string[]).includes(requested ?? '')
    ? (requested as Tab)
    : 'watchlists'

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Radar"
        description="Watchlists, catalysts and alerts for the setups you're stalking."
      />

      <Tabs value={tab} onValueChange={(value) => setSearchParams({ tab: value })}>
        {/* h-auto: the base TabsList fixes h-9, which clips the second row when
            the five triggers wrap on narrow viewports. */}
        <TabsList className="h-auto flex-wrap">
          <TabsTrigger value="watchlists">Watchlists</TabsTrigger>
          <TabsTrigger value="calendar">Calendar</TabsTrigger>
          <TabsTrigger value="news">News</TabsTrigger>
          <TabsTrigger value="sentiment">Sentiment</TabsTrigger>
          <TabsTrigger value="alerts">Alerts</TabsTrigger>
        </TabsList>
        <TabsContent value="watchlists" className="mt-4">
          <WatchlistsSection />
        </TabsContent>
        <TabsContent value="calendar" className="mt-4">
          <CalendarSection />
        </TabsContent>
        <TabsContent value="news" className="mt-4">
          <NewsSection />
        </TabsContent>
        <TabsContent value="sentiment" className="mt-4">
          <SentimentSection />
        </TabsContent>
        <TabsContent value="alerts" className="mt-4">
          <AlertsSection />
        </TabsContent>
      </Tabs>
    </div>
  )
}
