package com.linkgallery.companion.media

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class Pocket3MediaClassifierTest {
    @Test
    fun explicitPocket3AlbumClassifiesOriginalWithoutInventingAnEdit() {
        val result = Pocket3MediaClassifier.classify(
            fileName = "DJI_20260717124401_0001_D.JPG",
            albumName = "DJI Pocket 3",
            relativePath = "DCIM/DJI Pocket 3/",
            mimeType = "image/jpeg",
        )

        assertEquals(Pocket3MediaClassifier.POCKET_3, result.sourceDevice)
        assertNull(result.sourceApplication)
        assertFalse(result.isEditedExport)
    }

    @Test
    fun mimoExportUsesApplicationAndExplicitEditEvidence() {
        val result = Pocket3MediaClassifier.classify(
            fileName = "DJI_20260717124401_export.mp4",
            albumName = "DJI Pocket 3 Mimo Export",
            relativePath = "Movies/DJI Mimo/Pocket 3/Exported/",
            mimeType = "video/mp4",
            ownerPackageName = "dji.mimo",
        )

        assertEquals(Pocket3MediaClassifier.POCKET_3, result.sourceDevice)
        assertEquals(Pocket3MediaClassifier.DJI_MIMO, result.sourceApplication)
        assertTrue(result.isEditedExport)
    }

    @Test
    fun mimoDirectoryWithoutExportMarkerDoesNotGuessEdited() {
        val result = Pocket3MediaClassifier.classify(
            fileName = "DJI_20260717124401_0002_D.MP4",
            albumName = "DJI Mimo",
            relativePath = "DCIM/DJI Mimo/Pocket 3/",
            mimeType = "video/mp4",
        )

        assertEquals(Pocket3MediaClassifier.POCKET_3, result.sourceDevice)
        assertEquals(Pocket3MediaClassifier.DJI_MIMO, result.sourceApplication)
        assertFalse(result.isEditedExport)
    }

    @Test
    fun weakDjiNameAloneRemainsUnknown() {
        val result = Pocket3MediaClassifier.classify(
            fileName = "DJI_20260717124401_0003_D.JPG",
            albumName = "Camera",
            relativePath = "DCIM/Camera/",
            mimeType = "image/jpeg",
        )

        assertNull(result.sourceDevice)
        assertNull(result.sourceApplication)
        assertFalse(result.isEditedExport)
    }

    @Test
    fun genericDjiAlbumDoesNotMisclassifyDroneAsPocket3() {
        val result = Pocket3MediaClassifier.classify(
            fileName = "DJI_20260717124401_0004_D.JPG",
            albumName = "DJI Album",
            relativePath = "DCIM/DJI Album/",
            mimeType = "image/jpeg",
            metadataMake = "DJI",
            metadataModel = "FC3582",
        )

        assertNull(result.sourceDevice)
        assertNull(result.sourceApplication)
    }

    @Test
    fun actionAndPocket2EvidenceRemainUnknownDevice() {
        for (model in listOf("Osmo Action 5 Pro", "DJI Pocket 2")) {
            val result = Pocket3MediaClassifier.classify(
                fileName = "DJI_20260717124401_0005_D.MP4",
                albumName = "DJI Mimo",
                relativePath = "DCIM/DJI Mimo/",
                mimeType = "video/mp4",
                metadataMake = "DJI",
                metadataModel = model,
            )

            assertNull(result.sourceDevice)
            assertEquals(Pocket3MediaClassifier.DJI_MIMO, result.sourceApplication)
        }
    }

    @Test
    fun unrelatedMimoTextAndNonMediaRemainUnknown() {
        val result = Pocket3MediaClassifier.classify(
            fileName = "DJI_20260717124401_export.txt",
            albumName = "DJI Mimo Export",
            relativePath = "Documents/DJI Mimo/Export/",
            mimeType = "text/plain",
        )

        assertEquals(MediaSourceClassification(), result)
    }
}
