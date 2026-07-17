package com.linkgallery.companion

internal class DesktopActivityTracker(
    private val activeLifetimeMillis: Long,
) {
    private val lock = Any()
    private val lastSeen = mutableMapOf<String, Long>()

    init {
        require(activeLifetimeMillis > 0)
    }

    fun record(desktopId: String, nowMillis: Long) = synchronized(lock) {
        lastSeen[desktopId] = maxOf(lastSeen[desktopId] ?: Long.MIN_VALUE, nowMillis)
    }

    fun activeIds(nowMillis: Long): Set<String> = synchronized(lock) {
        removeExpired(nowMillis)
        lastSeen.keys.toSet()
    }

    fun nextExpiryDelayMillis(nowMillis: Long): Long? = synchronized(lock) {
        removeExpired(nowMillis)
        lastSeen.values.minOfOrNull { seenAt ->
            (seenAt + activeLifetimeMillis - nowMillis).coerceAtLeast(1)
        }
    }

    private fun removeExpired(nowMillis: Long) {
        lastSeen.entries.removeAll { (_, seenAt) ->
            nowMillis >= seenAt && nowMillis - seenAt >= activeLifetimeMillis
        }
    }
}
