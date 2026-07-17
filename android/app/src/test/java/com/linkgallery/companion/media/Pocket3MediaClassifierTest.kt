package com.linkgallery.companion.media

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class Pocket3MediaClassifierTest {
    @Test
    fun djiNameAndDjiAlbumClassifyPocketOriginalWithoutInventingAnEdit() {
        val result = Pocket3MediaClassifier.classify(
            fileName = "DJI_20260717124401_0001_D.JPG",
            albumName = "DJI Album",
            relativePath = "DCIM/DJI Album/",
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
            albumName = "DJI Mimo Export",
            relativePath = "Movies/DJI Mimo/Exported/",
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
            relativePath = "DCIM/DJI Mimo/",
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
