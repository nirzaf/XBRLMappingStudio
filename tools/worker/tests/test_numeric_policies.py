import json
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import arelle_worker as worker


class NumericPolicyTests(unittest.TestCase):
    def test_shared_financial_fixtures(self):
        path = Path(__file__).resolve().parents[3] / 'tests/XBRLMappingStudio.XBRL.Tests/fixtures/concept-numeric-policies.json'
        for fixture in json.loads(path.read_text()):
            with self.subTest(scenario=fixture['scenario']):
                if fixture['valid']:
                    worker.validate_numeric_policy_value(fixture['policy'], fixture['value'], fixture['decimals'])
                else:
                    with self.assertRaises(ValueError):
                        worker.validate_numeric_policy_value(fixture['policy'], fixture['value'], fixture['decimals'])

    def test_malformed_policies_fail_closed(self):
        for policy in ({'maximumSignificantDigits': 0}, {'roundingMode': 'nearest'},
                       {'roundingDecimals': -29}, {'minimumDecimals': 3, 'maximumDecimals': 2},
                       {'allowInfiniteAccuracy': 'false'}, {'unexpected': 1}):
            with self.subTest(policy=policy), self.assertRaises(ValueError):
                worker.validate_numeric_policy(policy)

    def test_worker_validates_expanded_concept_identity_and_skips_nil(self):
        profile = {'concepts': [{'namespace': 'urn:test', 'localName': 'Revenue',
                                'numericPolicy': {'roundingDecimals': 2}}]}
        fact = SimpleNamespace(qname=SimpleNamespace(namespaceURI='urn:test', localName='Revenue'),
                               isNil=False, value='1.255', decimals='2')
        model = SimpleNamespace(factsInInstance=[fact])
        with self.assertRaises(ValueError):
            worker.validate_model_numeric_policies([model], profile)
        fact.isNil = True
        worker.validate_model_numeric_policies([model], profile)
