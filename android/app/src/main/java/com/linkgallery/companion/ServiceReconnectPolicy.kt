package com.linkgallery.companion

internal class ServiceReconnectPolicy(
    private val initialDelayMillis: Long = 500,
    private val maximumDelayMillis: Long = 30_000,
) {
    private var failures = 0

    init {
        require(initialDelayMillis > 0)
        require(maximumDelayMillis >= initialDelayMillis)
    }

    fun nextDelayMillis(): Long {
        val shift = failures.coerceAtMost(30)
        failures++
        return (initialDelayMillis * (1L shl shift)).coerceAtMost(maximumDelayMillis)
    }

    fun reset() {
        failures = 0
    }
}
