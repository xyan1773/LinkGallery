package com.linkgallery.companion.server

import com.linkgallery.companion.pairing.AuthenticatedPairing

data class TransferStatusUpdate(
    val taskId: String,
    val destinationName: String,
    val completedItems: Int,
    val totalItems: Int,
    val completedBytes: Long,
    val totalBytes: Long,
    val state: String,
    val sequence: Long,
    val expiresAtEpochMillis: Long,
)

data class ActiveTransferStatus(
    val taskId: String,
    val desktopId: String,
    val desktopName: String,
    val destinationName: String,
    val completedItems: Int,
    val totalItems: Int,
    val completedBytes: Long,
    val totalBytes: Long,
    val state: String,
    val expiresAtEpochMillis: Long,
) {
    val progress: Float
        get() = when {
            totalBytes > 0 -> (completedBytes.toDouble() / totalBytes).toFloat()
            totalItems > 0 -> completedItems.toFloat() / totalItems
            else -> 0f
        }.coerceIn(0f, 1f)
}

sealed interface TransferStatusResult {
    data class Accepted(val status: ActiveTransferStatus?) : TransferStatusResult
    data class Rejected(val status: Int, val code: String, val message: String) : TransferStatusResult
}

class TransferStatusRegistry(
    private val nowMillis: () -> Long = System::currentTimeMillis,
    private val onChanged: (ActiveTransferStatus?) -> Unit = {},
) {
    private val latestSequence = mutableMapOf<String, Long>()
    private var active: ActiveTransferStatus? = null

    @Synchronized
    fun update(
        desktop: AuthenticatedPairing,
        update: TransferStatusUpdate,
    ): TransferStatusResult {
        validate(update)?.let { return it }
        val previousSequence = latestSequence[desktop.desktopId]
        if (previousSequence != null && update.sequence <= previousSequence) {
            return rejected(409, "transfer_status_replayed", "The transfer status sequence was already used.")
        }
        latestSequence[desktop.desktopId] = update.sequence
        if (update.state in TERMINAL_STATES) {
            if (active?.desktopId == desktop.desktopId) active = null
            onChanged(active)
            return TransferStatusResult.Accepted(active)
        }
        active = ActiveTransferStatus(
            taskId = update.taskId,
            desktopId = desktop.desktopId,
            desktopName = desktop.desktopName,
            destinationName = update.destinationName,
            completedItems = update.completedItems,
            totalItems = update.totalItems,
            completedBytes = update.completedBytes,
            totalBytes = update.totalBytes,
            state = update.state,
            expiresAtEpochMillis = update.expiresAtEpochMillis,
        )
        onChanged(active)
        return TransferStatusResult.Accepted(active)
    }

    @Synchronized
    fun current(): ActiveTransferStatus? {
        if (active?.expiresAtEpochMillis?.let { it <= nowMillis() } == true) {
            active = null
            onChanged(null)
        }
        return active
    }

    private fun validate(update: TransferStatusUpdate): TransferStatusResult.Rejected? {
        if (!TASK_ID.matches(update.taskId)) {
            return rejected(400, "invalid_transfer_status", "The task ID is invalid.")
        }
        if (!isSafeDisplayName(update.destinationName)) {
            return rejected(400, "invalid_transfer_status", "The destination display name is invalid.")
        }
        if (update.state !in ALLOWED_STATES || update.sequence <= 0) {
            return rejected(400, "invalid_transfer_status", "The transfer state or sequence is invalid.")
        }
        if (update.completedItems !in 0..update.totalItems || update.totalItems <= 0 ||
            update.completedBytes !in 0..update.totalBytes || update.totalBytes < 0
        ) {
            return rejected(400, "invalid_transfer_status", "The transfer progress is invalid.")
        }
        val now = nowMillis()
        if (update.expiresAtEpochMillis <= now || update.expiresAtEpochMillis > now + MAX_TTL_MILLIS) {
            return rejected(400, "invalid_transfer_status", "The transfer status expiry is invalid.")
        }
        return null
    }

    private fun isSafeDisplayName(value: String): Boolean =
        value.isNotBlank() && value.length <= 80 &&
            value.none { it.isISOControl() || it == '/' || it == '\\' || it == ':' } &&
            value.trim() !in setOf(".", "..")

    private fun rejected(status: Int, code: String, message: String) =
        TransferStatusResult.Rejected(status, code, message)

    private companion object {
        const val MAX_TTL_MILLIS = 120_000L
        val TASK_ID = Regex("^[A-Za-z0-9_-]{1,80}$")
        val ALLOWED_STATES = setOf("running", "paused", "completed", "cancelled", "failed")
        val TERMINAL_STATES = setOf("completed", "cancelled", "failed")
    }
}
