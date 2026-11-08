"""Independent algorithm oracle, not a complete DTS/filing qualification.

The shared table uses lightweight relationship objects with the *pinned Arelle*
XDT algorithm. Only its ModelConcept isinstance guard is adapted to our objects;
inheritance, targetRole, member usability and cube evaluation run unchanged.
"""
import json
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from arelle import ValidateXbrlDimensions as xdt


class Concept:
    isTypedDimension = False


class Relationships:
    def __init__(self, relationships):
        self.relationships = relationships

    def fromModelObject(self, concept):
        return [rel for rel in self.relationships if rel.fromModelObject is concept]

    def toModelObject(self, concept):
        return [rel for rel in self.relationships if rel.toModelObject is concept]


class ExplicitHypercubeOracleTests(unittest.TestCase):
    def test_shared_relationship_expectations_against_pinned_arelle(self):
        fixture_path = Path(__file__).resolve().parents[3] / 'tests/XBRLMappingStudio.XBRL.Tests/fixtures/explicit-hypercubes.json'
        fixtures = json.loads(fixture_path.read_text())
        self.assertEqual(20, len(fixtures))
        for fixture in fixtures:
            with self.subTest(scenario=fixture['scenario']):
                concepts = {}

                def concept(name):
                    return concepts.setdefault(name, Concept())

                relationships = [SimpleNamespace(
                    fromModelObject=concept(link['parentQName']),
                    toModelObject=concept(link['childQName']),
                    arcrole=link['arcRoleUri'], linkrole=link['roleUri'],
                    targetRole=link.get('targetRoleUri'),
                    isClosed=link.get('closed', False),
                    contextElement=link.get('contextElement'),
                    isUsable=link.get('usable', True)) for link in fixture['links']]
                model = SimpleNamespace(
                    relationshipSet=lambda arcrole, role=None: Relationships([
                        rel for rel in relationships if rel.arcrole == arcrole
                        and (role is None or rel.linkrole == role)]),
                    dimensionDefaultConcepts={rel.fromModelObject: rel.toModelObject for rel in relationships
                        if rel.arcrole == 'http://xbrl.org/int/dim/arcrole/dimension-default'})
                dims = {concept(dim['name']): SimpleNamespace(member=concept(dim['memberName']))
                        for dim in fixture['dimensions']}
                fact = SimpleNamespace(concept=concept('synthetic:Revenue'), context=SimpleNamespace(
                    dimValues=lambda element: dims if element == 'segment' else {},
                    nonDimValues=lambda element: []))
                with patch.object(xdt, 'ModelConcept', Concept):
                    self.assertEqual(fixture['valid'], xdt.isFactDimensionallyValid(
                        SimpleNamespace(modelXbrl=model), fact))


if __name__ == '__main__':
    unittest.main()
